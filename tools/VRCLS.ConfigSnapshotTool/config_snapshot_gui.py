# -*- coding: utf-8 -*-
"""Paste-based VRCLS config snapshot viewer.

This is intentionally standalone: it does not import the VRCLS C# projects and
only needs the Python standard library.
"""

from __future__ import annotations

import base64
import gzip
import json
import os
import re
import subprocess
import sys
import tkinter as tk
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path
from tkinter import filedialog, messagebox, ttk
from typing import Any, Iterable


FIELD_RE = re.compile(
    r"(?P<key>reason|algorithm|jsonBytes|compressedBytes|base64)=(?P<value>\S+)",
    re.IGNORECASE,
)
WRAPPED_SNAPSHOT_RE = re.compile(
    r"(?P<head>[^\r\n]*(?:CONFIG_SNAPSHOT_V1|配置快照压缩数据已记录)[^\r\n]*base64=)"
    r"(?P<base64>[A-Za-z0-9+/=\s]+)",
    re.IGNORECASE,
)
TIMESTAMP_RE = re.compile(
    r"^\[(?P<timestamp>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}(?:\.\d{1,7})?)"
)
SNAPSHOT_MARKERS = ("CONFIG_SNAPSHOT_V1", "配置快照压缩数据已记录")
SENSITIVE_MARKERS = ("password", "token", "api_key", "apikey", "secret")
LANGUAGE_MASK_LABELS = (
    (1, "原文"),
    (2, "译文1"),
    (4, "译文2"),
    (8, "译文3"),
)
SOURCE_MASK_LABELS = (
    (1, "麦克风"),
    (2, "监听音频"),
    (4, "拍照翻译"),
)
BITMASK_LABELS = {
    "micttsspeakmask": LANGUAGE_MASK_LABELS,
    "desktopttsspeakmask": LANGUAGE_MASK_LABELS,
    "mainwindowlanguagemask": LANGUAGE_MASK_LABELS,
    "copylanguagemask": LANGUAGE_MASK_LABELS,
    "steamvrpanellanguagemask": LANGUAGE_MASK_LABELS,
    "steamvrpanelsourcemask": SOURCE_MASK_LABELS,
}
BITMASK_NAME_VALUES = {
    "original": 1,
    "translation1": 2,
    "translation2": 4,
    "translation3": 8,
    "mic": 1,
    "desktop": 2,
    "photo": 4,
    "none": 0,
}


@dataclass(frozen=True)
class SnapshotEntry:
    index: int
    source: str
    line_number: int
    timestamp: datetime | None
    reason: str | None
    algorithm: str | None
    json_bytes: int | None
    compressed_bytes: int | None
    base64_text: str
    raw_line: str

    @property
    def source_name(self) -> str:
        return Path(self.source).name if self.source else ""

    @property
    def title(self) -> str:
        stamp = self.timestamp.strftime("%Y-%m-%d %H:%M:%S") if self.timestamp else f"#{self.index:04d}"
        reason = self.reason or "unknown"
        return f"{stamp}  {reason}"

    @property
    def detail(self) -> str:
        line = f":{self.line_number}" if self.line_number else ""
        size = f"{self.json_bytes:,} bytes" if self.json_bytes is not None else "unknown size"
        return f"{self.source_name}{line}  {size}"


@dataclass(frozen=True)
class Mapping:
    path: str
    area: str
    item: str
    notes: str = ""


@dataclass(frozen=True)
class ConfigRow:
    area: str
    item: str
    path: str
    snapshot_value: str
    snapshot_raw: str
    notes: str
    is_mapped: bool
    order: int


def default_log_directory() -> Path:
    appdata = os.environ.get("APPDATA")
    if appdata:
        return Path(appdata) / "VRCLS" / "logs"
    return Path.home() / "AppData" / "Roaming" / "VRCLS" / "logs"


def parse_int(value: str | None) -> int | None:
    if not value:
        return None
    try:
        return int(value)
    except ValueError:
        return None


def parse_timestamp(line: str) -> datetime | None:
    match = TIMESTAMP_RE.search(line)
    if not match:
        return None
    text = match.group("timestamp")
    for fmt in ("%Y-%m-%d %H:%M:%S.%f", "%Y-%m-%d %H:%M:%S"):
        try:
            return datetime.strptime(text, fmt)
        except ValueError:
            continue
    return None


def parse_snapshot_line(line: str, source: str, line_number: int, index: int) -> SnapshotEntry | None:
    if "base64=" not in line or not any(marker in line for marker in SNAPSHOT_MARKERS):
        return None

    fields: dict[str, str] = {}
    for match in FIELD_RE.finditer(line):
        fields[match.group("key").lower()] = match.group("value").strip().rstrip(";,")

    base64_text = fields.get("base64")
    if not base64_text:
        return None

    return SnapshotEntry(
        index=index,
        source=source,
        line_number=line_number,
        timestamp=parse_timestamp(line),
        reason=fields.get("reason"),
        algorithm=fields.get("algorithm"),
        json_bytes=parse_int(fields.get("jsonbytes")),
        compressed_bytes=parse_int(fields.get("compressedbytes")),
        base64_text=base64_text,
        raw_line=line,
    )


def parse_log_text(text: str, source: str = "粘贴内容", start_index: int = 0) -> list[SnapshotEntry]:
    entries: list[SnapshotEntry] = []
    index = start_index
    for line_number, line in enumerate(text.splitlines(), start=1):
        parsed = parse_snapshot_line(line, source, line_number, index + 1)
        if parsed is None:
            continue
        index += 1
        entries.append(parsed)
    return entries


def parse_wrapped_log_text(text: str, source: str = "粘贴内容") -> list[SnapshotEntry]:
    entries: list[SnapshotEntry] = []
    for match in WRAPPED_SNAPSHOT_RE.finditer(text):
        head = match.group("head")
        base64_text = "".join(match.group("base64").split())
        if not base64_text:
            continue
        try:
            decode_snapshot_base64(base64_text)
        except Exception:
            continue

        fields: dict[str, str] = {}
        for field_match in FIELD_RE.finditer(head):
            fields[field_match.group("key").lower()] = field_match.group("value").strip().rstrip(";,")

        line_number = text[: match.start()].count("\n") + 1
        entries.append(
            SnapshotEntry(
                index=len(entries) + 1,
                source=source,
                line_number=line_number,
                timestamp=parse_timestamp(head),
                reason=fields.get("reason"),
                algorithm=fields.get("algorithm") or "GZip",
                json_bytes=parse_int(fields.get("jsonbytes")),
                compressed_bytes=parse_int(fields.get("compressedbytes")),
                base64_text=base64_text,
                raw_line=head + base64_text,
            )
        )
    return entries


def create_direct_base64_entry(text: str) -> SnapshotEntry | None:
    normalized = "".join(text.split())
    if len(normalized) < 16:
        return None
    try:
        decode_snapshot_base64(normalized)
    except Exception:
        return None
    return SnapshotEntry(
        index=1,
        source="base64",
        line_number=0,
        timestamp=None,
        reason="direct",
        algorithm="GZip",
        json_bytes=None,
        compressed_bytes=None,
        base64_text=normalized,
        raw_line="",
    )


def decode_snapshot_base64(value: str) -> str:
    compressed = base64.b64decode(value.strip(), validate=False)
    return gzip.decompress(compressed).decode("utf-8")


def decode_snapshot(entry: SnapshotEntry) -> str:
    if entry.algorithm and entry.algorithm.lower() != "gzip":
        raise ValueError(f"Unsupported snapshot algorithm: {entry.algorithm}")
    return decode_snapshot_base64(entry.base64_text)


def load_log_entries_from_directory(directory: Path) -> list[SnapshotEntry]:
    entries: list[SnapshotEntry] = []
    index = 0
    for path in sorted(directory.glob("*.log"), key=lambda item: (item.stat().st_mtime, str(item).lower())):
        try:
            with path.open("r", encoding="utf-8", errors="replace") as handle:
                for line_number, line in enumerate(handle, start=1):
                    parsed = parse_snapshot_line(line.rstrip("\r\n"), str(path), line_number, index + 1)
                    if parsed is None:
                        continue
                    index += 1
                    entries.append(parsed)
        except OSError:
            continue
    return entries


def canonical_value(value: Any, exists: bool) -> str:
    if not exists:
        return ""
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":"))


def is_sensitive_path(path: str) -> bool:
    normalized = path.lower()
    return any(marker in normalized for marker in SENSITIVE_MARKERS)


def format_bitmask_value(value: Any, path: str) -> str | None:
    labels = BITMASK_LABELS.get(path.lower())
    if labels is None:
        return None

    if isinstance(value, bool):
        return None

    known_bits = 0
    for bit, _label in labels:
        known_bits |= bit

    try:
        mask = int(value)
    except (TypeError, ValueError):
        if not isinstance(value, str):
            return None

        mask = 0
        matched = False
        for token in re.split(r"[,\s|]+", value):
            normalized = token.strip().lower()
            if not normalized:
                continue
            bit = known_bits if normalized == "all" else BITMASK_NAME_VALUES.get(normalized)
            if bit is None:
                continue
            mask |= bit
            matched = True

        if not matched:
            return None

    selected = [label for bit, label in labels if (mask & bit) != 0]
    unknown_bits = mask & ~known_bits
    suffix = f"；未知位 {unknown_bits}" if unknown_bits else ""
    if not selected:
        return f"未选择 ({mask}){suffix}"
    return f"{'、'.join(selected)} ({mask}){suffix}"


def format_value(value: Any, exists: bool, path: str) -> str:
    if not exists:
        return "(缺失)"
    if is_sensitive_path(path):
        return "(空)" if value in (None, "") else "已设置 (已脱敏)"
    bitmask_value = format_bitmask_value(value, path)
    if bitmask_value is not None:
        return bitmask_value
    if value is None:
        return "null"
    if isinstance(value, bool):
        return "true" if value else "false"
    if isinstance(value, (int, float)):
        return str(value)
    if isinstance(value, str):
        if value == "":
            return "(空字符串)"
        compact = value.replace("\r", "\\r").replace("\n", "\\n")
        return compact if len(compact) <= 240 else compact[:240] + "..."
    if isinstance(value, list):
        return f"数组 ({len(value)} 项)"
    if isinstance(value, dict):
        return f"对象 ({len(value)} 项)"
    return str(value)


def get_path_value(root: Any, path: str) -> tuple[bool, Any]:
    current = root
    for segment in path.split("."):
        if not isinstance(current, dict):
            return False, None
        found = False
        for key, value in current.items():
            if key.lower() == segment.lower():
                current = value
                found = True
                break
        if not found:
            return False, None
    return True, current


def add_display_paths(value: Any, prefix: str, paths: set[str], mapped_paths: set[str]) -> None:
    if isinstance(value, dict):
        if prefix and prefix in mapped_paths:
            paths.add(prefix)
        for key, child in value.items():
            child_path = key if not prefix else f"{prefix}.{key}"
            add_display_paths(child, child_path, paths, mapped_paths)
        return

    if prefix:
        paths.add(prefix)


def build_rows(snapshot: Any, filter_text: str = "") -> list[ConfigRow]:
    mapped_paths = {mapping.path for mapping in MAPPINGS}
    mapping_by_path = {mapping.path.lower(): (index, mapping) for index, mapping in enumerate(MAPPINGS)}
    paths: set[str] = set(mapped_paths)
    add_display_paths(snapshot, "", paths, mapped_paths)

    rows: list[ConfigRow] = []
    filter_norm = filter_text.casefold().strip()
    for path in paths:
        snapshot_exists, snapshot_value = get_path_value(snapshot, path)
        if not snapshot_exists:
            continue

        mapping_entry = mapping_by_path.get(path.lower())
        if mapping_entry:
            order, mapping = mapping_entry
            area = mapping.area
            item = mapping.item
            notes = mapping.notes
            is_mapped = True
        else:
            order = 10_000_000
            area = "未映射"
            item = "当前 UI 未直接暴露"
            notes = "内部字段或尚未登记到 UI 映射表。"
            is_mapped = False

        snapshot_canonical = canonical_value(snapshot_value, snapshot_exists)

        row = ConfigRow(
            area=area,
            item=item,
            path=path,
            snapshot_value=format_value(snapshot_value, snapshot_exists, path),
            snapshot_raw=snapshot_canonical,
            notes=notes,
            is_mapped=is_mapped,
            order=order,
        )

        if filter_norm and filter_norm not in " ".join(
            [
                row.area,
                row.item,
                row.path,
                row.snapshot_value,
                row.notes,
            ]
        ).casefold():
            continue

        rows.append(row)

    return sorted(rows, key=lambda item: (item.order, item.area, item.path.casefold()))


def open_in_explorer(path: Path) -> None:
    if sys.platform.startswith("win"):
        subprocess.Popen(["explorer.exe", str(path)])
    elif sys.platform == "darwin":
        subprocess.Popen(["open", str(path)])
    else:
        subprocess.Popen(["xdg-open", str(path)])


class ConfigSnapshotApp:
    def __init__(self, root: tk.Tk) -> None:
        self.root = root
        self.root.title("VRCLS 配置快照解析器")
        self.root.geometry("1320x820")
        self.root.minsize(980, 620)

        self.entries: list[SnapshotEntry] = []
        self.decoded_json_by_index: dict[int, str] = {}
        self.rows: list[ConfigRow] = []
        self.visible_rows: list[ConfigRow] = []

        self.show_only_mapped = tk.BooleanVar(value=False)
        self.filter_var = tk.StringVar()
        self.status_var = tk.StringVar(value="粘贴包含 CONFIG_SNAPSHOT_V1 的日志文本后点击解析。")

        self._build_styles()
        self._build_ui()

    def _build_styles(self) -> None:
        style = ttk.Style(self.root)
        try:
            style.theme_use("clam")
        except tk.TclError:
            pass
        style.configure("Treeview", rowheight=26)
        style.configure("Treeview.Heading", font=("", 10, "bold"))
        style.configure("Status.TLabel", foreground="#4b5563")

    def _build_ui(self) -> None:
        outer = ttk.Frame(self.root, padding=12)
        outer.pack(fill=tk.BOTH, expand=True)

        header = ttk.Frame(outer)
        header.pack(fill=tk.X)
        ttk.Label(header, text="VRCLS 配置快照解析器", font=("", 16, "bold")).pack(side=tk.LEFT)
        ttk.Label(header, text="仅解析粘贴快照，不读取当前配置", style="Status.TLabel").pack(side=tk.RIGHT)

        input_frame = ttk.LabelFrame(outer, text="粘贴日志或 base64")
        input_frame.pack(fill=tk.X, pady=(10, 8))

        self.input_text = tk.Text(input_frame, height=7, wrap=tk.NONE, undo=True)
        input_y = ttk.Scrollbar(input_frame, orient=tk.VERTICAL, command=self.input_text.yview)
        input_x = ttk.Scrollbar(input_frame, orient=tk.HORIZONTAL, command=self.input_text.xview)
        self.input_text.configure(yscrollcommand=input_y.set, xscrollcommand=input_x.set)
        self.input_text.grid(row=0, column=0, sticky="nsew", padx=(8, 0), pady=(8, 0))
        input_y.grid(row=0, column=1, sticky="ns", pady=(8, 0))
        input_x.grid(row=1, column=0, sticky="ew", padx=(8, 0))
        input_frame.columnconfigure(0, weight=1)

        buttons = ttk.Frame(input_frame)
        buttons.grid(row=2, column=0, columnspan=2, sticky="ew", padx=8, pady=8)
        ttk.Button(buttons, text="解析粘贴内容", command=self.parse_pasted).pack(side=tk.LEFT, padx=(0, 8))
        ttk.Button(buttons, text="清空", command=self.clear_all).pack(side=tk.LEFT, padx=(0, 8))
        ttk.Button(buttons, text="从日志目录加载", command=self.load_from_log_directory).pack(side=tk.LEFT, padx=(0, 8))
        ttk.Button(buttons, text="打开日志目录", command=self.open_log_directory).pack(side=tk.LEFT, padx=(0, 8))
        ttk.Button(buttons, text="导出所选快照 JSON", command=self.export_selected_json).pack(side=tk.LEFT, padx=(0, 8))

        filter_frame = ttk.Frame(outer)
        filter_frame.pack(fill=tk.X, pady=(0, 8))
        ttk.Checkbutton(filter_frame, text="只看有 UI 映射", variable=self.show_only_mapped, command=self.apply_row_filter).pack(
            side=tk.LEFT, padx=(0, 12)
        )
        ttk.Label(filter_frame, text="筛选").pack(side=tk.LEFT)
        filter_entry = ttk.Entry(filter_frame, textvariable=self.filter_var, width=42)
        filter_entry.pack(side=tk.LEFT, padx=(6, 12))
        self.filter_var.trace_add("write", lambda *_: self.apply_row_filter())
        ttk.Button(filter_frame, text="复制选中行", command=self.copy_selected_row).pack(side=tk.LEFT)

        paned = ttk.PanedWindow(outer, orient=tk.HORIZONTAL)
        paned.pack(fill=tk.BOTH, expand=True)

        left_frame = ttk.LabelFrame(paned, text="解析出的快照")
        right_frame = ttk.LabelFrame(paned, text="配置项和 VRCLS UI 对照")
        paned.add(left_frame, weight=1)
        paned.add(right_frame, weight=4)

        self.snapshot_list = tk.Listbox(left_frame, activestyle="dotbox", exportselection=False)
        list_scroll = ttk.Scrollbar(left_frame, orient=tk.VERTICAL, command=self.snapshot_list.yview)
        self.snapshot_list.configure(yscrollcommand=list_scroll.set)
        self.snapshot_list.pack(side=tk.LEFT, fill=tk.BOTH, expand=True, padx=(8, 0), pady=8)
        list_scroll.pack(side=tk.RIGHT, fill=tk.Y, pady=8)
        self.snapshot_list.bind("<<ListboxSelect>>", self.on_snapshot_selected)

        columns = ("area", "item", "path", "snapshot", "notes")
        self.row_tree = ttk.Treeview(right_frame, columns=columns, show="headings", selectmode="browse")
        headings = {
            "area": "设置页",
            "item": "UI 项",
            "path": "配置键",
            "snapshot": "快照值",
            "notes": "说明",
        }
        widths = {
            "area": 90,
            "item": 180,
            "path": 230,
            "snapshot": 430,
            "notes": 280,
        }
        for column in columns:
            self.row_tree.heading(column, text=headings[column])
            self.row_tree.column(column, width=widths[column], minwidth=60, anchor=tk.W)
        self.row_tree.tag_configure("diff", background="#fff3cd")
        self.row_tree.tag_configure("unmapped", foreground="#6b7280")
        tree_y = ttk.Scrollbar(right_frame, orient=tk.VERTICAL, command=self.row_tree.yview)
        tree_x = ttk.Scrollbar(right_frame, orient=tk.HORIZONTAL, command=self.row_tree.xview)
        self.row_tree.configure(yscrollcommand=tree_y.set, xscrollcommand=tree_x.set)
        self.row_tree.grid(row=0, column=0, sticky="nsew", padx=(8, 0), pady=(8, 0))
        tree_y.grid(row=0, column=1, sticky="ns", pady=(8, 0))
        tree_x.grid(row=1, column=0, sticky="ew", padx=(8, 0))
        self.row_tree.bind("<<TreeviewSelect>>", self.show_row_detail)

        detail_frame = ttk.LabelFrame(right_frame, text="选中行详情")
        detail_frame.grid(row=2, column=0, columnspan=2, sticky="ew", padx=8, pady=(8, 8))
        self.detail_text = tk.Text(detail_frame, height=5, wrap=tk.WORD)
        self.detail_text.pack(fill=tk.X, expand=False, padx=8, pady=8)
        self.detail_text.configure(state=tk.DISABLED)

        right_frame.rowconfigure(0, weight=1)
        right_frame.columnconfigure(0, weight=1)

        ttk.Label(outer, textvariable=self.status_var, style="Status.TLabel").pack(fill=tk.X, pady=(8, 0))

    def parse_pasted(self) -> None:
        text = self.input_text.get("1.0", tk.END).strip()
        if not text:
            self.status_var.set("请先粘贴包含 CONFIG_SNAPSHOT_V1 的日志行，或直接粘贴 GZip base64。")
            return

        entries = parse_log_text(text)
        if entries:
            valid_entries: list[SnapshotEntry] = []
            for entry in entries:
                try:
                    decode_snapshot(entry)
                except Exception:
                    continue
                valid_entries.append(entry)
            if len(valid_entries) != len(entries):
                wrapped_entries = parse_wrapped_log_text(text)
                entries = wrapped_entries or valid_entries

        if not entries:
            direct = create_direct_base64_entry(text)
            if direct:
                entries = [direct]

        if not entries:
            self.entries = []
            self.rows = []
            self.refresh_snapshot_list()
            self.apply_row_filter()
            self.status_var.set("粘贴内容里没有找到 CONFIG_SNAPSHOT_V1，也不像可解码的 GZip base64 快照。")
            return

        self.set_entries(entries, "粘贴内容")

    def load_from_log_directory(self) -> None:
        directory = default_log_directory()
        if not directory.exists():
            self.status_var.set(f"日志目录不存在：{directory}")
            return

        entries = load_log_entries_from_directory(directory)
        if not entries:
            self.set_entries([], str(directory))
            self.status_var.set(f"未在日志目录中找到 CONFIG_SNAPSHOT_V1：{directory}")
            return

        self.set_entries(entries, str(directory))

    def set_entries(self, entries: list[SnapshotEntry], source_label: str) -> None:
        self.entries = sorted(entries, key=lambda item: (item.timestamp or datetime.min, item.index), reverse=True)
        self.decoded_json_by_index.clear()
        self.refresh_snapshot_list()
        if self.entries:
            self.snapshot_list.selection_set(0)
            self.snapshot_list.activate(0)
            self.load_selected_snapshot(0)
        else:
            self.rows = []
            self.apply_row_filter()
        self.status_var.set(f"已从{source_label}解析出 {len(self.entries)} 个配置快照。")

    def refresh_snapshot_list(self) -> None:
        self.snapshot_list.delete(0, tk.END)
        for entry in self.entries:
            self.snapshot_list.insert(tk.END, f"{entry.title}    {entry.detail}")

    def on_snapshot_selected(self, _event: tk.Event[tk.Misc] | None = None) -> None:
        selection = self.snapshot_list.curselection()
        if not selection:
            return
        self.load_selected_snapshot(selection[0])

    def load_selected_snapshot(self, selected_index: int) -> None:
        entry = self.entries[selected_index]
        try:
            decoded_json = self.decoded_json_by_index.get(entry.index)
            if decoded_json is None:
                decoded_json = decode_snapshot(entry)
                self.decoded_json_by_index[entry.index] = decoded_json
            snapshot_config = json.loads(decoded_json)
            self.rows = build_rows(snapshot_config)
            self.apply_row_filter()
            line = f" 行 {entry.line_number}" if entry.line_number else ""
            self.status_var.set(
                f"已选择 {entry.title}（{entry.source_name}{line}）。配置项 {len(self.rows)} 项。"
            )
        except Exception as exc:
            self.rows = []
            self.apply_row_filter()
            self.status_var.set(f"解析所选配置快照失败：{exc}")
            messagebox.showerror("解析失败", str(exc), parent=self.root)

    def apply_row_filter(self) -> None:
        filter_norm = self.filter_var.get().casefold().strip()
        self.visible_rows = []
        self.row_tree.delete(*self.row_tree.get_children())

        for row in self.rows:
            if self.show_only_mapped.get() and not row.is_mapped:
                continue
            if filter_norm and filter_norm not in " ".join(
                [
                    row.area,
                    row.item,
                    row.path,
                    row.snapshot_value,
                    row.notes,
                ]
            ).casefold():
                continue

            tags: list[str] = []
            if not row.is_mapped:
                tags.append("unmapped")

            item_id = self.row_tree.insert(
                "",
                tk.END,
                values=(
                    row.area,
                    row.item,
                    row.path,
                    row.snapshot_value,
                    row.notes,
                ),
                tags=tuple(tags),
            )
            self.visible_rows.append(row)
            self.row_tree.set(item_id, "path", row.path)

        self.set_detail_text("")

    def selected_row(self) -> ConfigRow | None:
        selection = self.row_tree.selection()
        if not selection:
            return None
        item_id = selection[0]
        index = self.row_tree.index(item_id)
        if index < 0 or index >= len(self.visible_rows):
            return None
        return self.visible_rows[index]

    def show_row_detail(self, _event: tk.Event[tk.Misc] | None = None) -> None:
        row = self.selected_row()
        if not row:
            self.set_detail_text("")
            return
        detail = (
            f"设置页: {row.area}\n"
            f"UI 项: {row.item}\n"
            f"配置键: {row.path}\n"
            f"说明: {row.notes}\n"
            f"快照值: {row.snapshot_value}"
        )
        self.set_detail_text(detail)

    def set_detail_text(self, text: str) -> None:
        self.detail_text.configure(state=tk.NORMAL)
        self.detail_text.delete("1.0", tk.END)
        self.detail_text.insert("1.0", text)
        self.detail_text.configure(state=tk.DISABLED)

    def copy_selected_row(self) -> None:
        row = self.selected_row()
        if not row:
            self.status_var.set("没有选中配置项。")
            return
        text = "\t".join([row.area, row.item, row.path, row.snapshot_value, row.notes])
        self.root.clipboard_clear()
        self.root.clipboard_append(text)
        self.status_var.set("已复制选中行。")

    def export_selected_json(self) -> None:
        selection = self.snapshot_list.curselection()
        if not selection:
            self.status_var.set("没有选中快照。")
            return
        entry = self.entries[selection[0]]
        try:
            decoded_json = self.decoded_json_by_index.get(entry.index)
            if decoded_json is None:
                decoded_json = decode_snapshot(entry)
                self.decoded_json_by_index[entry.index] = decoded_json
            parsed = json.loads(decoded_json)
            pretty = json.dumps(parsed, ensure_ascii=False, indent=2)
        except Exception as exc:
            messagebox.showerror("导出失败", str(exc), parent=self.root)
            return

        default_name = f"config-snapshot-{entry.index:04d}-{entry.reason or 'unknown'}.json"
        target = filedialog.asksaveasfilename(
            parent=self.root,
            title="导出所选快照 JSON",
            initialfile=default_name,
            defaultextension=".json",
            filetypes=[("JSON files", "*.json"), ("All files", "*.*")],
        )
        if not target:
            return
        Path(target).write_text(pretty, encoding="utf-8")
        self.status_var.set(f"已导出：{target}")

    def open_log_directory(self) -> None:
        directory = default_log_directory()
        if not directory.exists():
            self.status_var.set(f"日志目录不存在：{directory}")
            return
        open_in_explorer(directory)

    def clear_all(self) -> None:
        self.input_text.delete("1.0", tk.END)
        self.entries = []
        self.rows = []
        self.visible_rows = []
        self.decoded_json_by_index.clear()
        self.refresh_snapshot_list()
        self.apply_row_filter()
        self.status_var.set("已清空粘贴内容和解析结果。")


def make_mapping() -> list[Mapping]:
    mappings: list[Mapping] = []

    def add(path: str, area: str, item: str, notes: str = "") -> None:
        mappings.append(Mapping(path, area, item, notes))

    add("userInfo.auth_mode", "用户中心", "登录模式")
    add("userInfo.username", "用户中心", "用户名 / 账号别名")
    add("userInfo.password", "用户中心", "旧版服务器密码", "敏感字段，界面只显示是否已设置。")
    add("userInfo.user_uuid", "用户中心", "用户 UUID")
    add("userInfo.email", "用户中心", "邮箱")
    add("userInfo.email_verified", "用户中心", "邮箱验证状态")
    add("userInfo.access_token", "用户中心", "官网访问 Token", "敏感字段，界面只显示是否已设置。")
    add("userInfo.refresh_token", "用户中心", "官网刷新 Token", "敏感字段，界面只显示是否已设置。")
    add("userInfo.expires_at", "用户中心", "登录过期时间")
    add("useLegacyBackendApi", "用户中心", "旧版后端兼容开关")
    add("baseurl", "用户中心", "服务地址 Base URL")
    add("api-ip", "用户中心", "旧版 API 地址")
    add("api-port", "用户中心", "旧版 API 端口")

    add("micName", "语音识别", "麦克风设备")
    add("autoStartMic", "语音识别", "启动时自动开启麦克风识别")
    add("voiceMode", "语音识别", "麦克风触发模式")
    add("voiceHotKey_new", "语音识别", "麦克风切换热键")
    add("micPressingKey", "语音识别", "麦克风按住说话按键")
    add("dynamicThreshold", "语音识别", "旧版动态阈值")
    add("customThreshold", "语音识别", "旧版麦克风阈值")
    add("gameMicName", "语音识别", "监听设备")
    add("autoStartDesktop", "语音识别", "启动时自动开启监听音频识别")
    add("desktopAudioSourceMode", "语音识别", "监听来源")
    add("appAudioTargetProcessId", "语音识别", "指定应用进程 ID")
    add("appAudioTargetProcessName", "语音识别", "指定应用进程名")
    add("gameVoiceMode", "语音识别", "监听音频触发模式")
    add("gameVoiceHotKey_new", "语音识别", "监听音频切换热键")
    add("capPressingKey", "语音识别", "监听音频按住说话按键")
    add("gameCustomThreshold", "语音识别", "旧版监听音频阈值")
    add("micVadMinSilenceDuration", "语音识别", "麦克风 VAD 最短静音")
    add("micVadThreshold", "语音识别", "麦克风 VAD 阈值")
    add("micVadMaxSegmentSeconds", "语音识别", "麦克风 VAD 最大片段")
    add("desktopVadMinSilenceDuration", "语音识别", "监听音频 VAD 最短静音")
    add("desktopVadThreshold", "语音识别", "监听音频 VAD 阈值")
    add("desktopVadMaxSegmentSeconds", "语音识别", "监听音频 VAD 最大片段")
    add("vadMinSilenceDuration", "语音识别", "旧版通用 VAD 最短静音")
    add("vadThreshold", "语音识别", "旧版通用 VAD 阈值")
    add("vadMaxSegmentSeconds", "语音识别", "旧版通用 VAD 最大片段")
    add("recognitionMode", "语音识别", "本地语音识别开关")
    add("localRecognitionModelType", "语音识别", "本地识别模型类型")
    add("localStreamingEndpointSilenceSeconds", "语音识别", "本地流式断句静音时长")
    add("sherpaModelDir", "语音识别", "Sherpa 模型目录")
    add("senseVoiceModelDir", "语音识别", "SenseVoice 模型目录")
    add("preModelsDir", "语音识别", "预置模型目录")
    add("vadModelPath", "语音识别", "VAD 模型路径")

    add("translateService", "翻译", "麦克风翻译引擎")
    add("translateServicecap", "翻译", "监听音频翻译引擎")
    add("translationServer", "翻译", "旧版全局翻译引擎")
    add("translatorsDefaultRegion", "翻译", "非开发者引擎区域")
    add("sourceLanguage", "翻译", "旧版识别语言")
    add("micSourceLanguage", "翻译", "麦克风识别语言")
    add("desktopSourceLanguage", "翻译", "监听音频识别语言")
    add("micChineseAsrEngine", "翻译", "麦克风中文模型")
    add("desktopChineseAsrEngine", "翻译", "监听音频中文模型")
    add("micTargetTranslationLanguage", "翻译", "麦克风第一翻译")
    add("micTargetTranslationLanguage2", "翻译", "麦克风第二翻译")
    add("micTargetTranslationLanguage3", "翻译", "麦克风第三翻译")
    add("desktopTargetTranslationLanguage", "翻译", "监听音频第一翻译")
    add("desktopTargetTranslationLanguage2", "翻译", "监听音频第二翻译")
    add("desktopTargetTranslationLanguage3", "翻译", "监听音频第三翻译")
    add("textSendTargetTranslationLanguage", "翻译", "文字发送第一翻译")
    add("textSendTargetTranslationLanguage2", "翻译", "文字发送第二翻译")
    add("textSendTargetTranslationLanguage3", "翻译", "文字发送第三翻译")
    add("photoTranslationMode", "翻译", "拍照翻译模式")
    add("photoTargetTranslationLanguage", "翻译", "拍照翻译目标语言")
    add("filteremoji", "翻译", "过滤 Emoji")
    add("mainWindowLanguageMask", "翻译", "主窗口显示字段")
    add("openai_config.api_key", "翻译", "OpenAI API Key", "敏感字段，界面只显示是否已设置。")
    add("openai_config.base_url", "翻译", "OpenAI Base URL")
    add("openai_config.model", "翻译", "OpenAI 模型")
    add("openai_config.temperature", "翻译", "OpenAI Temperature")
    add("openai_config.max_tokens", "翻译", "OpenAI Max Tokens")
    add("openai_config.extra_body", "翻译", "OpenAI Extra Body")
    add("openai_config.system_prompt", "翻译", "OpenAI System Prompt")
    add("selectedOpenAiPreset", "翻译", "OpenAI 预设模板")
    add("openaiPresetTemplates", "翻译", "OpenAI 预设模板列表")

    add("osc-ip", "VR集成", "OSC 发送目标 IP")
    add("osc-port", "VR集成", "OSC 发送目标端口")
    add("VRCChatboxformat_new", "VR集成", "Chatbox 消息模板")
    add("oscChatboxNotificationSfx", "VR集成", "OSC Chatbox 提示音")
    add("oscShutdown", "VR集成", "退出时清空 OSC")
    add("enableOscServer", "VR集成", "OSC 接收服务开关")
    add("oscServerIp", "VR集成", "OSC 接收 IP")
    add("oscServerPort", "VR集成", "OSC 接收端口")
    add("useExternalOscInputAsTextInput", "VR集成", "外部 OSC 作为文本输入")
    add("chatboxOscMixTemplate", "VR集成", "OSC 混合模板")
    add("textInSteamVR", "VR集成", "SteamVR 掌心字幕开关")
    add("steamVrAutoLaunch", "VR集成", "随 SteamVR 启动")
    add("steamVrDiagnosticLogging", "VR集成", "SteamVR 诊断日志")
    add("steamVrPanelSourceMask", "VR集成", "SteamVR 面板来源多选")
    add("steamVrPanelLanguageMask", "VR集成", "SteamVR 面板显示字段")
    add("SteamVRHad", "VR集成", "绑定手柄")
    add("SteamVRSize", "VR集成", "Overlay 大小")
    add("steamVrTrackingDeviceMode", "VR集成", "Tracking source")
    add("steamVrFontScale", "VR集成", "SteamVR 字体缩放")
    add("steamVrNonTextOpacity", "VR集成", "SteamVR 非文本透明度")
    add("steamVrAnchorMode", "VR集成", "面板锚点")
    add("steamVrOffsetX", "VR集成", "掌心 X 偏移")
    add("steamVrOffsetY", "VR集成", "掌心 Y 偏移")
    add("steamVrOffsetZ", "VR集成", "掌心 Z 偏移")
    add("steamVrBackOfHandOffsetX", "VR集成", "手背 X 偏移")
    add("steamVrBackOfHandOffsetY", "VR集成", "手背 Y 偏移")
    add("steamVrBackOfHandOffsetZ", "VR集成", "手背 Z 偏移")
    add("steamVrHeadOffsetX", "VR集成", "头部 X 偏移")
    add("steamVrHeadOffsetY", "VR集成", "头部 Y 偏移")
    add("steamVrHeadOffsetZ", "VR集成", "头部 Z 偏移")
    add("steamVrRotationX", "VR集成", "掌心 Rot X")
    add("steamVrRotationY", "VR集成", "掌心 Rot Y")
    add("steamVrRotationZ", "VR集成", "掌心 Rot Z")
    add("steamVrBackOfHandRotationX", "VR集成", "手背 Rot X")
    add("steamVrBackOfHandRotationY", "VR集成", "手背 Rot Y")
    add("steamVrBackOfHandRotationZ", "VR集成", "手背 Rot Z")
    add("steamVrHeadRotationX", "VR集成", "头部 Rot X")
    add("steamVrHeadRotationY", "VR集成", "头部 Rot Y")
    add("steamVrHeadRotationZ", "VR集成", "头部 Rot Z")
    add("SteamVrCursorFlipU", "VR集成", "光标 X 轴反转")
    add("SteamVrCursorFlipV", "VR集成", "光标 Y 轴反转")
    add("VRCBitmapLed_COLOR", "VR集成", "Bitmap LED 颜色模式")
    add("VRCBitmapLed_row", "VR集成", "Bitmap LED 行数")
    add("VRCBitmapLed_col", "VR集成", "Bitmap LED 列数")

    add("TTSToggle", "TTS", "启用 TTS 播报")
    add("TTSEngine", "TTS", "引擎类型")
    add("ttsProvider", "TTS", "服务端 TTS")
    add("TTSOutputName", "TTS", "输出设备")
    add("TTSVoiceName", "TTS", "Edge 内置音色")
    add("qwenTtsVoiceName", "TTS", "Qwen 内置音色")
    add("mossTtsVoiceName", "TTS", "MOSS 内置音色")
    add("omnivoiceTtsVoiceName", "TTS", "OmniVoice 音色描述")
    add("ttsSpeed", "TTS", "语速")
    add("ttsVolume", "TTS", "音量")
    add("ttsPitch", "TTS", "音调")
    add("qwenTtsToneMode", "TTS", "Qwen 语气控制")
    add("qwenTtsInstruct", "TTS", "Qwen 指定语气")
    add("qwenTtsVoiceMode", "TTS", "Qwen 音色来源")
    add("qwenTtsCloneReferenceAudioPath", "TTS", "Qwen 克隆参考音频")
    add("qwenTtsCloneReferenceText", "TTS", "Qwen 克隆参考文本")
    add("qwenTtsCloneLanguage", "TTS", "Qwen 克隆语言")
    add("mossTtsVoiceMode", "TTS", "MOSS 音色来源")
    add("mossTtsCloneReferenceAudioPath", "TTS", "MOSS 克隆参考音频")
    add("mossTtsCloneLanguage", "TTS", "MOSS 克隆语言")
    add("omnivoiceTtsVoiceMode", "TTS", "OmniVoice 音色来源")
    add("omnivoiceTtsCloneReferenceAudioPath", "TTS", "OmniVoice 克隆参考音频")
    add("omnivoiceTtsCloneReferenceText", "TTS", "OmniVoice 克隆参考文本")
    add("omnivoiceTtsCloneLanguage", "TTS", "OmniVoice 克隆语言")
    add("ttsVoiceClonePresetId", "TTS", "声音预设")
    add("ttsVoiceClonePresets", "TTS", "声音预设列表")
    add("ttsVoiceCloneProfiles", "TTS", "多语言声音克隆配置")
    add("ttsInterruptMode", "TTS", "播报中断策略")
    add("ttsMicPassthroughEnabled", "TTS", "麦克风透传开关")
    add("ttsMicPassthroughVolume", "TTS", "麦克风透传音量")
    add("MicTtsSpeakMask", "TTS", "麦克风输入后播报字段")
    add("DesktopTtsSpeakMask", "TTS", "监听音频识别后播报字段")

    add("controlEnabled", "语音控制", "启用实验功能")
    add("commandFeedbackEnabled", "语音控制", "命中规则时播放提示音")
    add("voiceCommandKeywords", "语音控制", "实验性关键词规则")
    add("defaultScripts", "语音控制", "默认动作脚本")
    add("scripts", "语音控制", "自定义动作脚本")

    add("uiLanguage", "外观", "界面语言")
    add("darkmode", "外观", "深色模式")
    add("transparentWindow", "外观", "主窗口透明模式")
    add("transparentWindowSize", "外观", "主窗口透明尺寸")
    add("overlayWidth", "外观", "主窗口默认宽度")
    add("overlayHeight", "外观", "主窗口默认高度")
    add("mainWindowPinned", "外观", "主窗口置顶")
    add("mainWindowLeft", "外观", "主窗口左侧位置")
    add("mainWindowTop", "外观", "主窗口顶部位置")
    add("mainWindowOpacity", "外观", "主窗口整体透明度")
    add("mainWindowBackgroundOpacity", "外观", "主窗口背景透明度")
    add("steamVrMicTextColor", "外观", "SteamVR 麦克风文字颜色")
    add("steamVrDesktopTextColor", "外观", "SteamVR 监听音频文字颜色")
    add("CopyBox", "外观", "剪贴板辅助")
    add("copyLanguageMask", "外观", "剪贴板复制字段")

    add("appUpdateFeedUrl", "更新", "应用更新 Feed URL")
    add("updateServerUrl", "更新", "静态内容服务器")
    add("updateChannel", "更新", "更新通道")
    add("updateClientId", "更新", "更新客户端 ID")
    add("autoCheckUpdate", "更新", "自动检查应用更新")
    add("autoDownloadUpdate", "更新", "自动下载应用更新")
    add("updateCheckIntervalHours", "更新", "更新检查间隔")
    add("lastUpdateCheckUtc", "更新", "上次检查更新时间")
    add("autoDownloadModels", "更新", "自动下载模型包")
    add("translationProfileVersion", "更新", "翻译配置版本")
    add("autoSyncTranslationProfile", "更新", "自动同步翻译配置")
    add("supportedTranslationEngines", "更新", "已支持翻译引擎列表")
    add("supportedRecognitionLanguages", "更新", "已支持识别语言列表")
    add("supportedTranslationLanguages", "更新", "已支持翻译语言列表")
    add("translatorsRuntimeVersion", "更新", "翻译运行时版本")
    add("translatorsRuntimeDir", "更新", "翻译运行时目录")

    return mappings


MAPPINGS = make_mapping()


def main() -> None:
    root = tk.Tk()
    app = ConfigSnapshotApp(root)
    root.mainloop()


if __name__ == "__main__":
    main()
