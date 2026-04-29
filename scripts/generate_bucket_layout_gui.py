#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import mimetypes
import os
import re
import shutil
import subprocess
import threading
import traceback
import xml.etree.ElementTree as ET
from pathlib import Path
from urllib.error import HTTPError, URLError
from urllib.parse import urljoin
from urllib.request import Request, urlopen

import tkinter as tk
from tkinter import filedialog, messagebox, scrolledtext, ttk

import generate_bucket_layout as generator


R2_REQUIRED_KEYS = (
    "R2_ACCOUNT_ID",
    "R2_ACCESS_KEY_ID",
    "R2_ACCESS_KEY_SECRET",
    "R2_BUCKET_NAME",
    "R2_ENDPOINT_URL",
)
R2_OPTIONAL_KEYS = ("R2_CUSTOM_DOMAIN", "R2_PUBLIC_DOMAIN")
MULTIPART_THRESHOLD_BYTES = 50 * 1024 * 1024
MULTIPART_CHUNK_BYTES = 20 * 1024 * 1024
PRESET_FILE_NAME = ".release_publish_gui_presets.json"
NETSPARKLE_PUBLIC_KEY_NAMES = ("VRCLS_NETSPARKLE_PUBLIC_KEY", "SPARKLE_PUBLIC_KEY")
NETSPARKLE_PRIVATE_KEY_NAMES = ("VRCLS_NETSPARKLE_PRIVATE_KEY", "SPARKLE_PRIVATE_KEY")
NETSPARKLE_PUBLIC_KEY_FILE = "NetSparkle_Ed25519.pub"
NETSPARKLE_PRIVATE_KEY_FILE = "NetSparkle_Ed25519.priv"
KEYSTORE_JSON_FILE = "netsparkle-keys.json"


def resolve_repo_root() -> Path:
    return Path(__file__).resolve().parent.parent


def resolve_app_data_dir() -> Path:
    base = (os.environ.get("APPDATA") or "").strip()
    if base:
        return Path(base).expanduser().resolve()
    return (Path.home() / "AppData" / "Roaming").resolve()


def get_default_key_store_dir() -> Path:
    return resolve_app_data_dir() / "VRCLS" / "release-publisher" / "keys"


def parse_dotenv(path: Path) -> dict[str, str]:
    values: dict[str, str] = {}
    if not path.is_file():
        return values

    for raw_line in path.read_text(encoding="utf-8").splitlines():
        line = raw_line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, value = line.split("=", 1)
        key = key.strip()
        value = value.strip().strip('"').strip("'")
        if key:
            values[key] = value
    return values


def find_env_file() -> Path | None:
    for candidate in (resolve_repo_root() / ".env", Path.cwd() / ".env"):
        if candidate.is_file():
            return candidate.resolve()
    return None


def load_optional_env_values() -> tuple[dict[str, str], Path | None]:
    env_path = find_env_file()
    env_values = parse_dotenv(env_path) if env_path is not None else {}
    return env_values, env_path


def resolve_optional_setting(names: tuple[str, ...], env_values: dict[str, str]) -> str:
    for name in names:
        value = (os.environ.get(name) or env_values.get(name) or "").strip()
        if value:
            return value
    return ""


def resolve_inno_setup_compiler() -> str:
    candidates: list[str] = []
    explicit = (os.environ.get("INNO_SETUP_COMPILER") or "").strip()
    if explicit:
        candidates.append(explicit)

    for path_dir in os.environ.get("PATH", "").split(os.pathsep):
        if not path_dir.strip():
            continue
        candidates.append(str(Path(path_dir) / "iscc.exe"))

    program_files = [os.environ.get("ProgramFiles"), os.environ.get("ProgramFiles(x86)")]
    for base_dir in program_files:
        if not base_dir:
            continue
        candidates.append(str(Path(base_dir) / "Inno Setup 6" / "ISCC.exe"))
        candidates.append(str(Path(base_dir) / "Inno Setup 5" / "ISCC.exe"))

    for candidate in candidates:
        if candidate and Path(candidate).is_file():
            return str(Path(candidate).resolve())

    return ""


def resolve_appcast_generator() -> str:
    candidates: list[str] = []

    for path_dir in os.environ.get("PATH", "").split(os.pathsep):
        if not path_dir.strip():
            continue
        candidates.append(str(Path(path_dir) / "netsparkle-generate-appcast.exe"))
        candidates.append(str(Path(path_dir) / "netsparkle-generate-appcast"))

    user_profile = (os.environ.get("USERPROFILE") or "").strip()
    if user_profile:
        candidates.append(str(Path(user_profile) / ".dotnet" / "tools" / "netsparkle-generate-appcast.exe"))
        candidates.append(str(Path(user_profile) / ".dotnet" / "tools" / "netsparkle-generate-appcast"))

    seen: set[str] = set()
    for candidate in candidates:
        normalized = str(Path(candidate))
        if normalized in seen:
            continue
        seen.add(normalized)
        if Path(candidate).is_file():
            return str(Path(candidate).resolve())

    return ""


def ensure_appcast_generator() -> str:
    existing = resolve_appcast_generator()
    if existing:
        return existing

    completed = subprocess.run(
        ["dotnet", "tool", "install", "-g", "NetSparkleUpdater.Tools.AppCastGenerator", "--version", "2.8.6"],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        check=False,
    )
    installed = resolve_appcast_generator()
    if installed:
        return installed

    stderr = (completed.stderr or completed.stdout or "").strip()
    raise SystemExit(stderr or "Failed to install NetSparkleUpdater.Tools.AppCastGenerator.")


def save_netsparkle_keys(store_dir: Path, public_key: str, private_key: str) -> Path:
    store_dir.mkdir(parents=True, exist_ok=True)
    payload = {
        "schema": 1,
        "generated_utc": None,
        "public_key": public_key.strip(),
        "private_key": private_key.strip(),
    }
    json_path = store_dir / KEYSTORE_JSON_FILE
    json_path.write_text(json.dumps(payload, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
    (store_dir / NETSPARKLE_PUBLIC_KEY_FILE).write_text(public_key.strip() + "\n", encoding="utf-8", newline="\n")
    (store_dir / NETSPARKLE_PRIVATE_KEY_FILE).write_text(private_key.strip() + "\n", encoding="utf-8", newline="\n")
    return json_path


def load_netsparkle_keys(store_dir: Path) -> tuple[str, str]:
    json_path = store_dir / KEYSTORE_JSON_FILE
    if json_path.is_file():
        payload = json.loads(json_path.read_text(encoding="utf-8"))
        public_key = str(payload.get("public_key", "")).strip()
        private_key = str(payload.get("private_key", "")).strip()
        if public_key and private_key:
            return public_key, private_key

    pub_path = store_dir / NETSPARKLE_PUBLIC_KEY_FILE
    priv_path = store_dir / NETSPARKLE_PRIVATE_KEY_FILE
    public_key = pub_path.read_text(encoding="utf-8").strip() if pub_path.is_file() else ""
    private_key = priv_path.read_text(encoding="utf-8").strip() if priv_path.is_file() else ""
    return public_key, private_key


def load_r2_settings() -> tuple[dict[str, str], Path]:
    env_path = find_env_file()
    if env_path is None:
        raise SystemExit("R2 .env file not found in repository root.")

    env_values = parse_dotenv(env_path)
    settings: dict[str, str] = {}
    missing: list[str] = []
    for key in (*R2_REQUIRED_KEYS, *R2_OPTIONAL_KEYS):
        settings[key] = (os.environ.get(key) or env_values.get(key) or "").strip()
    for key in R2_REQUIRED_KEYS:
        if not settings[key]:
            missing.append(key)
    if missing:
        raise SystemExit("Missing required R2 settings in .env: " + ", ".join(missing))
    return settings, env_path


def build_public_base(settings: dict[str, str]) -> str:
    raw = (settings.get("R2_CUSTOM_DOMAIN") or settings.get("R2_PUBLIC_DOMAIN") or "").strip()
    if not raw:
        return ""
    if not raw.startswith(("http://", "https://")):
        raw = f"https://{raw}"
    return raw.rstrip("/")


def build_public_url(settings: dict[str, str], key: str) -> str:
    public_base = build_public_base(settings)
    return f"{public_base}/{key.lstrip('/')}" if public_base else ""


def build_url_from_base(base_url: str, relative_path: str) -> str:
    normalized_base = (base_url or "").strip()
    if not normalized_base:
        return ""
    if not normalized_base.endswith("/"):
        normalized_base += "/"
    return urljoin(normalized_base, relative_path.lstrip("/"))


def normalize_file_name_segment(value: str) -> str:
    normalized = (value or "").strip()
    if not normalized:
        return "unknown"
    normalized = re.sub(r'[<>:"/\\\\|?*]+', "-", normalized)
    normalized = normalized.replace(" ", "-")
    normalized = re.sub(r"-{2,}", "-", normalized)
    return normalized.strip("-") or "unknown"


def collect_upload_files(output_root: Path) -> list[Path]:
    return sorted(path for path in output_root.rglob("*") if path.is_file())


def get_upload_key_prefix(output_root: Path) -> str:
    repo_root = resolve_repo_root()
    app_release_dirs = (
        repo_root / "Output" / "app-release",
        repo_root / "Output" / "app-releases",
    )

    for app_release_dir in app_release_dirs:
        try:
            if output_root.resolve() == app_release_dir.resolve():
                return "app-releases"
        except Exception:
            pass

    if output_root.name.strip().lower() in {"app-release", "app-releases"}:
        return "app-releases"

    return ""


def build_upload_key(output_root: Path, file_path: Path) -> str:
    relative_path = file_path.relative_to(output_root).as_posix()
    key_prefix = get_upload_key_prefix(output_root)
    if not key_prefix:
        return relative_path
    return f"{key_prefix.rstrip('/')}/{relative_path.lstrip('/')}"


def format_file_size(size_bytes: int) -> str:
    size = float(size_bytes)
    for unit in ("B", "KB", "MB", "GB", "TB"):
        if size < 1024 or unit == "TB":
            return f"{int(size)} {unit}" if unit == "B" else f"{size:.2f} {unit}"
        size /= 1024
    return f"{size_bytes} B"


def is_dangerous_upload_root(output_root: Path) -> bool:
    repo_output_root = resolve_repo_root() / "Output"
    try:
        if output_root.resolve() == repo_output_root.resolve():
            return True
    except Exception:
        pass
    return output_root.name.strip().lower() == "output"


def build_upload_preflight_message(output_root: Path, files: list[Path]) -> tuple[str, bool]:
    total_size = sum(path.stat().st_size for path in files)
    key_prefix = get_upload_key_prefix(output_root)
    preview_items = [build_upload_key(output_root, path) for path in files[:12]]
    dangerous = is_dangerous_upload_root(output_root)

    lines = [
        f"Upload folder: {output_root}",
        f"Remote prefix: /{key_prefix}/" if key_prefix else "Remote prefix: /(bucket root)",
        f"Files: {len(files)}",
        f"Total size: {format_file_size(total_size)}",
        "",
        "Remote key preview:",
    ]
    lines.extend(f"- {item}" for item in preview_items)
    if len(files) > len(preview_items):
        lines.append(f"- ... and {len(files) - len(preview_items)} more files")
    if dangerous:
        lines.extend(["", "Warning:", "Top-level Output may contain historical installers."])
    elif len(files) > 50:
        lines.extend(["", "Warning:", "This upload contains many files."])
    lines.extend(["", "Continue upload?"])
    return "\n".join(lines), dangerous


def fetch_status_code(url: str) -> tuple[int | None, str]:
    try:
        request = Request(url, method="GET")
        with urlopen(request, timeout=15) as response:
            return getattr(response, "status", 200), "OK"
    except HTTPError as exc:
        return exc.code, exc.reason or "HTTP error"
    except URLError as exc:
        return None, str(exc.reason)
    except Exception as exc:
        return None, str(exc)


def collect_verification_urls(output_root: Path, public_base_url: str) -> list[str]:
    if not public_base_url:
        return []

    urls: list[str] = []
    installer_candidates = sorted(output_root.glob("VRCLS-*-setup.exe"))
    if installer_candidates:
        installer_path = installer_candidates[0]
        urls.append(build_url_from_base(public_base_url, build_upload_key(output_root, installer_path)))

    for relative_path in (
        "appcast.xml",
        "appcast.xml.signature",
        "upload-manifest.json",
        "manifests/models/list.json",
        "manifests/translations/profile.json",
    ):
        if (output_root / Path(relative_path)).is_file():
            urls.append(build_url_from_base(public_base_url, build_upload_key(output_root, output_root / Path(relative_path))))
    return urls


def create_r2_client():
    try:
        import boto3
        from boto3.s3.transfer import TransferConfig
        from botocore.config import Config as BotoConfig
    except ImportError as exc:
        raise SystemExit("Missing dependency: boto3. Run `pip install boto3` first.") from exc
    return boto3, TransferConfig, BotoConfig


def read_project_versions(project_path: Path) -> dict[str, str]:
    root = ET.fromstring(project_path.read_text(encoding="utf-8"))

    def find_text(tag_name: str) -> str:
        for element in root.iter(tag_name):
            if element.text and element.text.strip():
                return element.text.strip()
        return ""

    return {
        "version": find_text("Version"),
        "file_version": find_text("FileVersion"),
        "assembly_version": find_text("AssemblyVersion"),
        "informational_version": find_text("InformationalVersion"),
    }


def write_project_versions(project_path: Path, internal_version: str, display_version: str) -> None:
    root = ET.fromstring(project_path.read_text(encoding="utf-8"))
    internal_version = (internal_version or "").strip()
    display_version = (display_version or "").strip()
    if not internal_version or not all(part.isdigit() for part in internal_version.split(".")):
        raise SystemExit("Internal Version must be numeric, for example 0.7.0.116.")
    if not display_version:
        raise SystemExit("Display Version is required.")

    updates = {
        "Version": internal_version,
        "FileVersion": internal_version,
        "AssemblyVersion": internal_version,
        "InformationalVersion": display_version,
    }

    for property_group in root.findall("PropertyGroup"):
        for child in property_group:
            if child.tag in updates:
                child.text = updates[child.tag]

    ET.indent(root, space="  ")
    xml_text = ET.tostring(root, encoding="unicode")
    project_path.write_text(xml_text + "\n", encoding="utf-8", newline="\n")


class ReleasePublisherGui:
    def __init__(self, root: tk.Tk) -> None:
        self.root = root
        self.root.title("VRCLS Release Publisher")
        self.root.geometry("980x760")

        repo_root = resolve_repo_root()
        self.project_path = repo_root / "03.VRCLS.UI" / "03.VRCLS.UI.csproj"
        self.last_output_root = ""
        self.is_running = False

        self.output_root = tk.StringVar(value=str(repo_root / "bucket-generated"))
        self.base_url = tk.StringVar(value=generator.DEFAULT_BASE_URL)
        self.publish_source = tk.StringVar(value="")
        self.preset_name = tk.StringVar(value="")
        self.preset_file_path = tk.StringVar(value=str(repo_root / PRESET_FILE_NAME))

        self.app_version = tk.StringVar(value="")
        self.display_version = tk.StringVar(value="")
        self.channel = tk.StringVar(value="stable")
        self.build_configuration = tk.StringVar(value="Release")
        self.build_runtime = tk.StringVar(value="win-x64")
        self.build_output = tk.StringVar(value="publish")
        self.build_with_premodels = tk.BooleanVar(value=False)
        env_values, _ = load_optional_env_values()
        self.key_store_dir = tk.StringVar(value=str(get_default_key_store_dir()))
        self.inno_setup_compiler = tk.StringVar(value=resolve_inno_setup_compiler())
        self.netsparkle_public_key = tk.StringVar(value=resolve_optional_setting(NETSPARKLE_PUBLIC_KEY_NAMES, env_values))
        self.netsparkle_private_key = tk.StringVar(value=resolve_optional_setting(NETSPARKLE_PRIVATE_KEY_NAMES, env_values))

        self.vad_file = tk.StringVar(value="")
        self.vad_version = tk.StringVar(value="1.0.0")
        self.copy_vad_file = tk.BooleanVar(value=True)
        self.sherpa_path = tk.StringVar(value="")
        self.sherpa_version = tk.StringVar(value="2023-02-20")
        self.sherpa_dir_name = tk.StringVar(value=generator.DEFAULT_SHERPA_DIR_NAME)
        self.copy_sherpa_package = tk.BooleanVar(value=True)

        self.presets: dict[str, dict[str, object]] = {}
        self.build_release_button: ttk.Button | None = None
        self.build_model_button: ttk.Button | None = None
        self.upload_button: ttk.Button | None = None
        self.verify_button: ttk.Button | None = None
        self.open_output_button: ttk.Button | None = None
        self.generate_keys_button: ttk.Button | None = None
        self.save_keys_button: ttk.Button | None = None
        self.load_keys_button: ttk.Button | None = None
        self.open_keys_button: ttk.Button | None = None
        self.preset_combobox: ttk.Combobox | None = None
        self.log_box: scrolledtext.ScrolledText | None = None

        self._load_presets_from_disk()
        self._load_versions_from_project()
        self._load_keys_from_store_if_available()
        self._build_ui()

    def _build_ui(self) -> None:
        container = ttk.Frame(self.root, padding=12)
        container.pack(fill=tk.BOTH, expand=True)
        container.columnconfigure(0, weight=1)
        container.rowconfigure(4, weight=1)

        self._build_publish_section(container).grid(row=0, column=0, sticky="ew", pady=(0, 8))
        self._build_release_section(container).grid(row=1, column=0, sticky="ew", pady=(0, 8))
        self._build_model_section(container).grid(row=2, column=0, sticky="ew", pady=(0, 8))
        self._build_log_section(container).grid(row=4, column=0, sticky="nsew", pady=(0, 8))
        self._build_action_bar(container).grid(row=5, column=0, sticky="ew")

    def _build_publish_section(self, parent: ttk.Frame) -> ttk.LabelFrame:
        frame = ttk.LabelFrame(parent, text="Publish")
        frame.columnconfigure(1, weight=1)

        self._add_entry_row(frame, 0, "Publish Source", self.publish_source, browse_dir=True)
        self._add_entry_row(frame, 1, "Model Output Root", self.output_root, browse_dir=True)
        self._add_entry_row(frame, 2, "Base URL", self.base_url)
        self._add_entry_row(frame, 3, "Preset File", self.preset_file_path, browse_file=True, file_types=[("JSON files", "*.json"), ("All files", "*.*")])

        ttk.Label(frame, text="Preset").grid(row=4, column=0, sticky="w", padx=6, pady=4)
        self.preset_combobox = ttk.Combobox(frame, textvariable=self.preset_name, state="normal")
        self.preset_combobox.grid(row=4, column=1, sticky="ew", padx=6, pady=4)
        self._refresh_preset_names()

        button_bar = ttk.Frame(frame)
        button_bar.grid(row=4, column=2, sticky="w", padx=6, pady=4)
        ttk.Button(button_bar, text="Save Preset", command=self.on_save_preset).pack(side=tk.LEFT)
        ttk.Button(button_bar, text="Load Preset", command=self.on_load_preset).pack(side=tk.LEFT, padx=(6, 0))
        return frame

    def _build_release_section(self, parent: ttk.Frame) -> ttk.LabelFrame:
        frame = ttk.LabelFrame(parent, text="App Release")
        frame.columnconfigure(1, weight=1)
        frame.columnconfigure(3, weight=1)

        self._add_entry_row(frame, 0, "Internal Version", self.app_version)
        self._add_entry_row(frame, 1, "Display Version", self.display_version)

        ttk.Label(frame, text="Channel").grid(row=0, column=2, sticky="w", padx=6, pady=4)
        ttk.Entry(frame, textvariable=self.channel).grid(row=0, column=3, sticky="ew", padx=6, pady=4)
        ttk.Label(frame, text="Configuration").grid(row=2, column=0, sticky="w", padx=6, pady=4)
        ttk.Combobox(frame, textvariable=self.build_configuration, values=("Release", "Debug"), state="readonly").grid(row=2, column=1, sticky="ew", padx=6, pady=4)
        ttk.Label(frame, text="Runtime").grid(row=2, column=2, sticky="w", padx=6, pady=4)
        ttk.Entry(frame, textvariable=self.build_runtime).grid(row=2, column=3, sticky="ew", padx=6, pady=4)
        ttk.Label(frame, text="Build Output").grid(row=3, column=0, sticky="w", padx=6, pady=4)
        ttk.Entry(frame, textvariable=self.build_output).grid(row=3, column=1, sticky="ew", padx=6, pady=4)
        ttk.Checkbutton(frame, text="Include pre-models", variable=self.build_with_premodels).grid(row=3, column=2, columnspan=2, sticky="w", padx=6, pady=4)
        self._add_entry_row(frame, 4, "Inno Compiler", self.inno_setup_compiler, browse_file=True, file_types=[("ISCC", "ISCC.exe"), ("Executable", "*.exe"), ("All files", "*.*")])
        self._add_entry_row(frame, 5, "Key Store Dir", self.key_store_dir, browse_dir=True)

        key_button_bar = ttk.Frame(frame)
        key_button_bar.grid(row=5, column=2, columnspan=2, sticky="w", padx=6, pady=4)
        self.generate_keys_button = ttk.Button(key_button_bar, text="Generate Keys", command=self.on_generate_keys)
        self.generate_keys_button.pack(side=tk.LEFT)
        self.save_keys_button = ttk.Button(key_button_bar, text="Save Keys", command=self.on_save_keys)
        self.save_keys_button.pack(side=tk.LEFT, padx=(6, 0))
        self.load_keys_button = ttk.Button(key_button_bar, text="Load Keys", command=self.on_load_keys)
        self.load_keys_button.pack(side=tk.LEFT, padx=(6, 0))
        self.open_keys_button = ttk.Button(key_button_bar, text="Open Key Folder", command=self.on_open_key_store)
        self.open_keys_button.pack(side=tk.LEFT, padx=(6, 0))

        self._add_entry_row(frame, 6, "NetSparkle Public Key", self.netsparkle_public_key)
        ttk.Label(frame, text="NetSparkle Private Key").grid(row=7, column=0, sticky="w", padx=6, pady=4)
        ttk.Entry(frame, textvariable=self.netsparkle_private_key, show="*").grid(row=7, column=1, columnspan=3, sticky="ew", padx=6, pady=4)
        return frame

    def _build_model_section(self, parent: ttk.Frame) -> ttk.LabelFrame:
        frame = ttk.LabelFrame(parent, text="Model Feed")
        frame.columnconfigure(1, weight=1)
        frame.columnconfigure(3, weight=1)

        self._add_entry_row(frame, 0, "VAD File", self.vad_file, browse_file=True, file_types=[("ONNX files", "*.onnx"), ("All files", "*.*")])
        ttk.Label(frame, text="VAD Version").grid(row=1, column=0, sticky="w", padx=6, pady=4)
        ttk.Entry(frame, textvariable=self.vad_version).grid(row=1, column=1, sticky="ew", padx=6, pady=4)
        ttk.Checkbutton(frame, text="Copy VAD into model output", variable=self.copy_vad_file).grid(row=1, column=2, columnspan=2, sticky="w", padx=6, pady=4)

        self._add_path_picker_row(frame, 2, "Sherpa Path", self.sherpa_path)
        ttk.Label(frame, text="Sherpa Version").grid(row=3, column=0, sticky="w", padx=6, pady=4)
        ttk.Entry(frame, textvariable=self.sherpa_version).grid(row=3, column=1, sticky="ew", padx=6, pady=4)
        ttk.Label(frame, text="Sherpa Dir Name").grid(row=3, column=2, sticky="w", padx=6, pady=4)
        ttk.Entry(frame, textvariable=self.sherpa_dir_name).grid(row=3, column=3, sticky="ew", padx=6, pady=4)
        ttk.Checkbutton(frame, text="Copy/build Sherpa package into model output", variable=self.copy_sherpa_package).grid(row=4, column=0, columnspan=4, sticky="w", padx=6, pady=4)
        return frame

    def _build_log_section(self, parent: ttk.Frame) -> ttk.LabelFrame:
        frame = ttk.LabelFrame(parent, text="Log")
        frame.columnconfigure(0, weight=1)
        frame.rowconfigure(0, weight=1)
        self.log_box = scrolledtext.ScrolledText(frame, wrap=tk.WORD, height=16)
        self.log_box.grid(row=0, column=0, sticky="nsew", padx=6, pady=6)
        self.log_box.insert(tk.END, "Ready.\n")
        self.log_box.configure(state=tk.DISABLED)
        return frame

    def _build_action_bar(self, parent: ttk.Frame) -> ttk.Frame:
        frame = ttk.Frame(parent)
        self.build_release_button = ttk.Button(frame, text="Build App Release + Installer", command=self.on_build_app_release)
        self.build_release_button.pack(side=tk.LEFT)
        self.build_model_button = ttk.Button(frame, text="Build Model Feed", command=self.on_generate)
        self.build_model_button.pack(side=tk.LEFT, padx=(8, 0))
        self.upload_button = ttk.Button(frame, text="Upload to R2", command=self.on_upload)
        self.upload_button.pack(side=tk.LEFT, padx=(8, 0))
        self.verify_button = ttk.Button(frame, text="Verify Public URLs", command=self.on_verify_urls)
        self.verify_button.pack(side=tk.LEFT, padx=(8, 0))
        self.open_output_button = ttk.Button(frame, text="Open Current Folder", command=self.open_output_folder)
        self.open_output_button.pack(side=tk.LEFT, padx=(8, 0))
        ttk.Button(frame, text="Fill Demo Paths", command=self.fill_demo_paths).pack(side=tk.LEFT, padx=(8, 0))
        return frame

    def _add_entry_row(self, parent: ttk.LabelFrame, row: int, label: str, variable: tk.Variable, browse_file: bool = False, browse_dir: bool = False, file_types: list[tuple[str, str]] | None = None) -> None:
        parent.columnconfigure(1, weight=1)
        ttk.Label(parent, text=label).grid(row=row, column=0, sticky="w", padx=6, pady=4)
        ttk.Entry(parent, textvariable=variable).grid(row=row, column=1, sticky="ew", padx=6, pady=4)
        if browse_file:
            ttk.Button(parent, text="Browse", command=lambda: self._browse_file(variable, file_types or [("All files", "*.*")])).grid(row=row, column=2, sticky="w", padx=6, pady=4)
        elif browse_dir:
            ttk.Button(parent, text="Browse", command=lambda: self._browse_dir(variable)).grid(row=row, column=2, sticky="w", padx=6, pady=4)

    def _add_path_picker_row(self, parent: ttk.LabelFrame, row: int, label: str, variable: tk.Variable) -> None:
        parent.columnconfigure(1, weight=1)
        ttk.Label(parent, text=label).grid(row=row, column=0, sticky="w", padx=6, pady=4)
        ttk.Entry(parent, textvariable=variable).grid(row=row, column=1, sticky="ew", padx=6, pady=4)
        bar = ttk.Frame(parent)
        bar.grid(row=row, column=2, columnspan=2, sticky="w", padx=6, pady=4)
        ttk.Button(bar, text="File", command=lambda: self._browse_file(variable, [("Zip files", "*.zip"), ("All files", "*.*")])).pack(side=tk.LEFT)
        ttk.Button(bar, text="Folder", command=lambda: self._browse_dir(variable)).pack(side=tk.LEFT, padx=(6, 0))

    def _browse_file(self, variable: tk.Variable, file_types: list[tuple[str, str]]) -> None:
        selected = filedialog.askopenfilename(initialdir=resolve_repo_root(), filetypes=file_types)
        if selected:
            variable.set(selected)

    def _browse_dir(self, variable: tk.Variable) -> None:
        selected = filedialog.askdirectory(initialdir=resolve_repo_root())
        if selected:
            variable.set(selected)

    def get_presets_path(self) -> Path:
        raw = self.preset_file_path.get().strip()
        return Path(raw).expanduser().resolve() if raw else resolve_repo_root() / PRESET_FILE_NAME

    def _load_versions_from_project(self) -> None:
        if not self.project_path.is_file():
            return
        try:
            versions = read_project_versions(self.project_path)
        except Exception:
            return
        if versions["version"] and not self.app_version.get().strip():
            self.app_version.set(versions["version"])
        if versions["informational_version"] and not self.display_version.get().strip():
            self.display_version.set(versions["informational_version"])

    def _load_presets_from_disk(self) -> None:
        self.presets = {}
        presets_path = self.get_presets_path()
        if not presets_path.is_file():
            return
        try:
            payload = json.loads(presets_path.read_text(encoding="utf-8"))
        except Exception:
            return
        presets = payload.get("presets", payload) if isinstance(payload, dict) else {}
        if not isinstance(presets, dict):
            return
        for name, item in presets.items():
            if isinstance(name, str) and isinstance(item, dict):
                self.presets[name] = item

    def _save_presets_to_disk(self) -> None:
        presets_path = self.get_presets_path()
        presets_path.parent.mkdir(parents=True, exist_ok=True)
        text = json.dumps({"schema": 2, "presets": self.presets}, ensure_ascii=False, indent=2) + "\n"
        presets_path.write_text(text, encoding="utf-8", newline="\n")

    def _refresh_preset_names(self) -> None:
        if self.preset_combobox is not None:
            self.preset_combobox["values"] = sorted(self.presets.keys(), key=str.lower)

    def _collect_form_state(self) -> dict[str, object]:
        return {
            "output_root": self.output_root.get().strip(),
            "base_url": self.base_url.get().strip(),
            "publish_source": self.publish_source.get().strip(),
            "app_version": self.app_version.get().strip(),
            "display_version": self.display_version.get().strip(),
            "channel": self.channel.get().strip(),
            "build_configuration": self.build_configuration.get().strip(),
            "build_runtime": self.build_runtime.get().strip(),
            "build_output": self.build_output.get().strip(),
            "build_with_premodels": bool(self.build_with_premodels.get()),
            "key_store_dir": self.key_store_dir.get().strip(),
            "inno_setup_compiler": self.inno_setup_compiler.get().strip(),
            "vad_file": self.vad_file.get().strip(),
            "vad_version": self.vad_version.get().strip(),
            "copy_vad_file": bool(self.copy_vad_file.get()),
            "sherpa_path": self.sherpa_path.get().strip(),
            "sherpa_version": self.sherpa_version.get().strip(),
            "sherpa_dir_name": self.sherpa_dir_name.get().strip(),
            "copy_sherpa_package": bool(self.copy_sherpa_package.get()),
        }

    def _apply_form_state(self, state: dict[str, object]) -> None:
        def read_text(key: str, default: str = "") -> str:
            value = state.get(key, default)
            return "" if value is None else str(value)

        def read_bool(key: str, default: bool = False) -> bool:
            value = state.get(key, default)
            if isinstance(value, bool):
                return value
            if isinstance(value, str):
                return value.strip().lower() in {"1", "true", "yes", "on"}
            return bool(value)

        self.output_root.set(read_text("output_root", str(resolve_repo_root() / "bucket-generated")))
        self.base_url.set(read_text("base_url", generator.DEFAULT_BASE_URL))
        self.publish_source.set(read_text("publish_source"))
        self.app_version.set(read_text("app_version"))
        self.display_version.set(read_text("display_version"))
        self.channel.set(read_text("channel", "stable"))
        self.build_configuration.set(read_text("build_configuration", "Release"))
        self.build_runtime.set(read_text("build_runtime", "win-x64"))
        self.build_output.set(read_text("build_output", "publish"))
        self.build_with_premodels.set(read_bool("build_with_premodels", False))
        self.key_store_dir.set(read_text("key_store_dir", str(get_default_key_store_dir())))
        self.inno_setup_compiler.set(read_text("inno_setup_compiler", resolve_inno_setup_compiler()))
        self.vad_file.set(read_text("vad_file"))
        self.vad_version.set(read_text("vad_version", "1.0.0"))
        self.copy_vad_file.set(read_bool("copy_vad_file", True))
        self.sherpa_path.set(read_text("sherpa_path"))
        self.sherpa_version.set(read_text("sherpa_version", "2023-02-20"))
        self.sherpa_dir_name.set(read_text("sherpa_dir_name", generator.DEFAULT_SHERPA_DIR_NAME))
        self.copy_sherpa_package.set(read_bool("copy_sherpa_package", True))
        self._load_keys_from_store_if_available()

    def on_save_preset(self) -> None:
        preset_name = self.preset_name.get().strip()
        if not preset_name:
            messagebox.showerror("Save Preset", "Preset name is required.")
            return
        self.presets[preset_name] = self._collect_form_state()
        self._save_presets_to_disk()
        self._refresh_preset_names()
        self.log(f"Preset saved: {preset_name} -> {self.get_presets_path()}")
        messagebox.showinfo("Save Preset", f"Preset saved:\n{preset_name}\n\nFile:\n{self.get_presets_path()}")

    def on_load_preset(self) -> None:
        self._load_presets_from_disk()
        self._refresh_preset_names()
        preset_name = self.preset_name.get().strip()
        if not preset_name:
            messagebox.showerror("Load Preset", "Preset name is required.")
            return
        preset = self.presets.get(preset_name)
        if preset is None:
            messagebox.showerror("Load Preset", f"Preset not found:\n{preset_name}\n\nFile:\n{self.get_presets_path()}")
            return
        self._apply_form_state(preset)
        self.preset_name.set(preset_name)
        self.log(f"Preset loaded: {preset_name} <- {self.get_presets_path()}")

    def _resolve_key_store_dir(self) -> Path:
        raw = self.key_store_dir.get().strip()
        return Path(raw).expanduser().resolve() if raw else get_default_key_store_dir()

    def _load_keys_from_store_if_available(self) -> bool:
        store_dir = self._resolve_key_store_dir()
        try:
            public_key, private_key = load_netsparkle_keys(store_dir)
        except Exception:
            return False
        if not public_key or not private_key:
            return False
        self.netsparkle_public_key.set(public_key)
        self.netsparkle_private_key.set(private_key)
        return True

    def on_load_keys(self) -> None:
        store_dir = self._resolve_key_store_dir()
        try:
            public_key, private_key = load_netsparkle_keys(store_dir)
        except Exception as exc:
            messagebox.showerror("Load Keys", str(exc))
            return
        if not public_key or not private_key:
            messagebox.showerror("Load Keys", f"No keys found in:\n{store_dir}")
            return
        self.netsparkle_public_key.set(public_key)
        self.netsparkle_private_key.set(private_key)
        self.log(f"Loaded NetSparkle keys from: {store_dir}")
        messagebox.showinfo("Load Keys", f"Loaded keys from:\n{store_dir}")

    def on_save_keys(self) -> None:
        public_key = self.netsparkle_public_key.get().strip()
        private_key = self.netsparkle_private_key.get().strip()
        if not public_key or not private_key:
            messagebox.showerror("Save Keys", "Public key and private key are both required.")
            return
        store_dir = self._resolve_key_store_dir()
        try:
            json_path = save_netsparkle_keys(store_dir, public_key, private_key)
        except Exception as exc:
            messagebox.showerror("Save Keys", str(exc))
            return
        self.log(f"Saved NetSparkle keys to: {json_path}")
        messagebox.showinfo("Save Keys", f"Keys saved to:\n{json_path}")

    def on_generate_keys(self) -> None:
        try:
            generator_path = ensure_appcast_generator()
            store_dir = self._resolve_key_store_dir()
            store_dir.mkdir(parents=True, exist_ok=True)
            temp_dir = store_dir / "_tmp_generate"
            if temp_dir.exists():
                shutil.rmtree(temp_dir, ignore_errors=True)
            temp_dir.mkdir(parents=True, exist_ok=True)
            completed = subprocess.run(
                [generator_path, "--generate-keys", "--force", "--key-path", str(temp_dir)],
                capture_output=True,
                text=True,
                encoding="utf-8",
                errors="replace",
                check=False,
            )
            if completed.returncode != 0:
                stderr = (completed.stderr or completed.stdout or "").strip()
                raise SystemExit(stderr or f"Key generation failed with exit code {completed.returncode}.")

            public_key = (temp_dir / NETSPARKLE_PUBLIC_KEY_FILE).read_text(encoding="utf-8").strip()
            private_key = (temp_dir / NETSPARKLE_PRIVATE_KEY_FILE).read_text(encoding="utf-8").strip()
            if not public_key or not private_key:
                raise SystemExit("Key generation completed, but the generated key files were empty.")

            json_path = save_netsparkle_keys(store_dir, public_key, private_key)
            self.netsparkle_public_key.set(public_key)
            self.netsparkle_private_key.set(private_key)
            self.log(f"Generated and stored NetSparkle keys at: {json_path}")
            messagebox.showinfo("Generate Keys", f"Generated and saved keys to:\n{json_path}")
        except SystemExit as exc:
            messagebox.showerror("Generate Keys", str(exc) or "Key generation failed.")
        except Exception as exc:
            messagebox.showerror("Generate Keys", str(exc))
        finally:
            temp_dir = self._resolve_key_store_dir() / "_tmp_generate"
            shutil.rmtree(temp_dir, ignore_errors=True)

    def on_open_key_store(self) -> None:
        store_dir = self._resolve_key_store_dir()
        store_dir.mkdir(parents=True, exist_ok=True)
        try:
            if os.name == "nt":
                os.startfile(str(store_dir))
            else:
                subprocess.Popen(["xdg-open", str(store_dir)])
        except Exception as exc:
            messagebox.showerror("Open Key Folder", str(exc))

    def fill_demo_paths(self) -> None:
        repo_root = resolve_repo_root()
        self.output_root.set(str(repo_root / "bucket-generated"))
        self.base_url.set(generator.DEFAULT_BASE_URL)
        vad_example = repo_root / "pre-models" / generator.DEFAULT_VAD_FILE_NAME
        sherpa_example = repo_root / "pre-models" / generator.DEFAULT_SHERPA_DIR_NAME
        if vad_example.exists():
            self.vad_file.set(str(vad_example))
        if sherpa_example.exists():
            self.sherpa_path.set(str(sherpa_example))
        if not self.inno_setup_compiler.get().strip():
            self.inno_setup_compiler.set(resolve_inno_setup_compiler())
        self.log("Filled demo paths from current repository.")

    def on_build_app_release(self) -> None:
        if self.is_running:
            return
        self._set_busy()
        self.log("Starting app release + installer build...")
        threading.Thread(target=self._run_build_app_release, daemon=True).start()

    def on_generate(self) -> None:
        if self.is_running:
            return
        try:
            args = self.build_args()
        except SystemExit as exc:
            messagebox.showerror("Invalid Input", str(exc))
            return
        self._set_busy()
        self.log("Starting model feed generation...")
        threading.Thread(target=self._run_generation, args=(args,), daemon=True).start()

    def _resolve_publish_source(self) -> str:
        return self.publish_source.get().strip() or self.output_root.get().strip()

    def _resolve_current_folder(self) -> str:
        return self.last_output_root or self.publish_source.get().strip() or self.output_root.get().strip()

    def on_upload(self) -> None:
        if self.is_running:
            return
        target = self._resolve_publish_source()
        if not target:
            messagebox.showerror("Upload to R2", "Publish source is empty.")
            return
        output_root = Path(target).expanduser().resolve()
        if not output_root.is_dir():
            messagebox.showerror("Upload to R2", f"Publish source does not exist:\n{output_root}")
            return
        files = collect_upload_files(output_root)
        if not files:
            messagebox.showerror("Upload to R2", f"No files found in publish source:\n{output_root}")
            return
        preflight_message, dangerous = build_upload_preflight_message(output_root, files)
        if dangerous:
            messagebox.showwarning("Upload to R2", preflight_message)
            return
        if not messagebox.askyesno("Upload to R2", preflight_message):
            self.log("Upload cancelled by user during preflight check.")
            return
        self._set_busy()
        self.log(f"Starting upload from: {output_root}")
        threading.Thread(target=self._run_upload, args=(output_root, files), daemon=True).start()

    def on_verify_urls(self) -> None:
        if self.is_running:
            return
        target = self._resolve_publish_source()
        if not target:
            messagebox.showerror("Verify Public URLs", "Publish source is empty.")
            return
        output_root = Path(target).expanduser().resolve()
        if not output_root.is_dir():
            messagebox.showerror("Verify Public URLs", f"Publish source does not exist:\n{output_root}")
            return
        public_base_url = self.base_url.get().strip()
        if not public_base_url:
            messagebox.showerror("Verify Public URLs", "Base URL is empty.")
            return
        self._set_busy()
        self.log(f"Starting public URL verification: {public_base_url}")
        threading.Thread(target=self._run_verify_urls, args=(output_root, public_base_url), daemon=True).start()

    def _run_generation(self, args: argparse.Namespace) -> None:
        try:
            output_root, created = generator.generate_layout(args)
            lines = ["Model feed generation completed.", f"Output: {output_root}", *[str(path) for path in created]]
            output_root_text = str(output_root)
            self.root.after(0, lambda output_root_text=output_root_text, lines=lines: self._finish_success(output_root_text, lines, "Model feed generated successfully."))
        except SystemExit as exc:
            error_message = str(exc) or "Generation failed."
            self.root.after(0, lambda error_message=error_message: self._finish_error(error_message))
        except Exception:
            error_message = traceback.format_exc()
            self.root.after(0, lambda error_message=error_message: self._finish_error(error_message))

    def _run_subprocess(self, command: list[str], cwd: Path) -> None:
        self._post_log("Build command:")
        self._post_log(" ".join(command))
        completed = subprocess.run(command, cwd=str(cwd), capture_output=True, text=True, encoding="utf-8", errors="replace", check=False)
        if completed.stdout:
            for line in completed.stdout.splitlines():
                if line.strip():
                    self._post_log(line)
        if completed.stderr:
            for line in completed.stderr.splitlines():
                if line.strip():
                    self._post_log(line)
        if completed.returncode != 0:
            raise SystemExit(f"Build failed with exit code {completed.returncode}.")

    def _run_build_app_release(self) -> None:
        try:
            repo_root = resolve_repo_root()
            use_premodels = self.build_with_premodels.get()
            output_name = self.build_output.get().strip() or ("publish_with_premodels" if use_premodels else "publish")
            runtime = self.build_runtime.get().strip() or "win-x64"
            channel = self.channel.get().strip() or "stable"
            env_values, env_path = load_optional_env_values()
            store_public_key, store_private_key = ("", "")
            try:
                store_public_key, store_private_key = load_netsparkle_keys(self._resolve_key_store_dir())
            except Exception:
                pass
            public_key = self.netsparkle_public_key.get().strip() or store_public_key or resolve_optional_setting(NETSPARKLE_PUBLIC_KEY_NAMES, env_values)
            private_key = self.netsparkle_private_key.get().strip() or store_private_key or resolve_optional_setting(NETSPARKLE_PRIVATE_KEY_NAMES, env_values)
            inno_compiler = self.inno_setup_compiler.get().strip() or resolve_inno_setup_compiler()

            missing_items: list[str] = []
            if not public_key:
                missing_items.append("NetSparkle public key (fill the GUI field, or set `VRCLS_NETSPARKLE_PUBLIC_KEY` / `SPARKLE_PUBLIC_KEY`)")
            if not private_key:
                missing_items.append("NetSparkle private key (fill the GUI field, or set `VRCLS_NETSPARKLE_PRIVATE_KEY` / `SPARKLE_PRIVATE_KEY`)")
            if not inno_compiler:
                missing_items.append("Inno Setup compiler (fill the GUI field, or set `INNO_SETUP_COMPILER`)")
            if missing_items:
                env_hint = f"\nChecked .env: {env_path}" if env_path is not None else "\nChecked .env: not found"
                raise SystemExit("Missing build prerequisites:\n- " + "\n- ".join(missing_items) + env_hint)

            write_project_versions(self.project_path, self.app_version.get().strip(), self.display_version.get().strip())
            self._post_log(f"Applied project version: internal={self.app_version.get().strip()}, display={self.display_version.get().strip()}")
            command = [
                "powershell", "-ExecutionPolicy", "Bypass", "-File", str(repo_root / "scripts" / "build-installer.ps1"),
                "-Configuration", self.build_configuration.get().strip() or "Release",
                "-Runtime", runtime,
                "-Output", output_name,
                "-InternalVersion", self.app_version.get().strip(),
                "-DisplayVersion", self.display_version.get().strip(),
                "-Channel", channel,
                "-InnoSetupCompiler", inno_compiler,
                "-NetSparklePublicKey", public_key,
                "-NetSparklePrivateKey", private_key,
            ]
            appcast_base_url = build_url_from_base(self.base_url.get().strip(), "app-releases/")
            if appcast_base_url:
                command.extend(["-AppCastBaseUrl", appcast_base_url.rstrip("/")])
            if use_premodels:
                command.append("-IncludePreModels")
            self._run_subprocess(command, repo_root)
            versions = read_project_versions(self.project_path)
            display_segment = normalize_file_name_segment(versions["informational_version"] or versions["version"])
            release_dir = repo_root / "Output" / "app-release"
            installer_path = repo_root / "Output" / f"VRCLS-{display_segment}-{runtime}-setup.exe"
            if not release_dir.is_dir():
                raise SystemExit(f"Build succeeded but release directory not found: {release_dir}")
            lines = ["App release + installer build completed.", f"Release Folder: {release_dir}"]
            if installer_path.is_file():
                lines.append(f"Installer: {installer_path}")
            version = versions["version"]
            informational_version = versions["informational_version"]
            self.root.after(
                0,
                lambda release_dir=release_dir, installer_path=installer_path, version=version, informational_version=informational_version, lines=lines:
                    self._finish_release_build_success(release_dir, installer_path, version, informational_version, lines),
            )
        except SystemExit as exc:
            error_message = str(exc) or "Build failed."
            self.root.after(0, lambda error_message=error_message: self._finish_error(error_message))
        except Exception:
            error_message = traceback.format_exc()
            self.root.after(0, lambda error_message=error_message: self._finish_error(error_message))

    def _run_upload(self, output_root: Path, files: list[Path]) -> None:
        try:
            settings, env_path = load_r2_settings()
            boto3, transfer_config_type, botocore_config_type = create_r2_client()
            client = boto3.client(service_name="s3", endpoint_url=settings["R2_ENDPOINT_URL"], aws_access_key_id=settings["R2_ACCESS_KEY_ID"], aws_secret_access_key=settings["R2_ACCESS_KEY_SECRET"], config=botocore_config_type(signature_version="s3v4", retries={"max_attempts": 3}), region_name="auto", verify=False)
            transfer_config = transfer_config_type(multipart_threshold=MULTIPART_THRESHOLD_BYTES, multipart_chunksize=MULTIPART_CHUNK_BYTES, max_concurrency=4, use_threads=True)
            key_prefix = get_upload_key_prefix(output_root)
            self._post_log(f"Loaded R2 config from: {env_path}")
            self._post_log(f"Bucket: {settings['R2_BUCKET_NAME']}")
            self._post_log(f"Remote prefix: /{key_prefix}/" if key_prefix else "Remote prefix: /(bucket root)")
            self._post_log(f"Files to upload: {len(files)}")
            uploaded_urls: list[str] = []
            for index, file_path in enumerate(files, start=1):
                key = build_upload_key(output_root, file_path)
                extra_args: dict[str, str] = {}
                content_type, _ = mimetypes.guess_type(file_path.name)
                if content_type:
                    extra_args["ContentType"] = content_type
                self._post_log(f"[{index}/{len(files)}] Uploading {key}")
                client.upload_file(str(file_path), settings["R2_BUCKET_NAME"], key, ExtraArgs=extra_args or None, Config=transfer_config)
                public_url = build_public_url(settings, key)
                if public_url and len(uploaded_urls) < 3:
                    uploaded_urls.append(public_url)
                self._post_log(f"[{index}/{len(files)}] Uploaded {key}")
            lines = ["Upload completed.", f"Source: {output_root}", f"Bucket: {settings['R2_BUCKET_NAME']}", f"Files uploaded: {len(files)}"]
            lines.append(f"Remote Prefix: /{key_prefix}/" if key_prefix else "Remote Prefix: /(bucket root)")
            public_base = build_public_base(settings)
            if public_base:
                lines.append(f"Public Base: {public_base}/")
                lines.extend(self._verify_public_urls(output_root, public_base))
            lines.extend(uploaded_urls)
            output_root_text = str(output_root)
            self.root.after(0, lambda output_root_text=output_root_text, lines=lines: self._finish_success(output_root_text, lines, "Upload to R2 completed successfully."))
        except SystemExit as exc:
            error_message = str(exc) or "Upload failed."
            self.root.after(0, lambda error_message=error_message: self._finish_error(error_message))
        except Exception:
            error_message = traceback.format_exc()
            self.root.after(0, lambda error_message=error_message: self._finish_error(error_message))

    def _run_verify_urls(self, output_root: Path, public_base_url: str) -> None:
        try:
            lines = [f"Verification base: {public_base_url}", *self._verify_public_urls(output_root, public_base_url)]
            output_root_text = str(output_root)
            self.root.after(0, lambda output_root_text=output_root_text, lines=lines: self._finish_success(output_root_text, lines, "Public URL verification completed."))
        except Exception:
            error_message = traceback.format_exc()
            self.root.after(0, lambda error_message=error_message: self._finish_error(error_message))

    def _verify_public_urls(self, output_root: Path, public_base_url: str) -> list[str]:
        urls = collect_verification_urls(output_root, public_base_url)
        if not urls:
            return ["Verification skipped: no matching release/model files in publish source."]
        lines = ["Public URL verification:"]
        for url in urls:
            status_code, message = fetch_status_code(url)
            lines.append(f"{'PASS' if status_code == 200 else 'FAIL'} {status_code if status_code is not None else message} {url}")
        return lines

    def _post_log(self, message: str) -> None:
        self.root.after(0, lambda: self.log(message))

    def _finish_release_build_success(self, release_dir: Path, installer_path: Path, internal_version: str, display_version: str, lines: list[str]) -> None:
        self.last_output_root = str(release_dir)
        self.publish_source.set(str(release_dir))
        if internal_version:
            self.app_version.set(internal_version)
        if display_version:
            self.display_version.set(display_version)
        for line in lines:
            self.log(line)
        self._set_idle()
        messagebox.showinfo("Done", "App release and installer built successfully.")

    def _finish_success(self, output_root: str, lines: list[str], message: str) -> None:
        self.last_output_root = output_root
        self.publish_source.set(output_root)
        for line in lines:
            self.log(line)
        self._set_idle()
        messagebox.showinfo("Done", message)

    def _finish_error(self, message: str) -> None:
        self.log("Operation failed:")
        self.log(message.rstrip())
        self._set_idle()
        messagebox.showerror("Failed", message)

    def _set_busy(self) -> None:
        self.is_running = True
        for button in (
            self.build_release_button,
            self.build_model_button,
            self.upload_button,
            self.verify_button,
            self.open_output_button,
            self.generate_keys_button,
            self.save_keys_button,
            self.load_keys_button,
            self.open_keys_button,
        ):
            if button is not None:
                button.configure(state=tk.DISABLED)

    def _set_idle(self) -> None:
        self.is_running = False
        for button in (
            self.build_release_button,
            self.build_model_button,
            self.upload_button,
            self.verify_button,
            self.open_output_button,
            self.generate_keys_button,
            self.save_keys_button,
            self.load_keys_button,
            self.open_keys_button,
        ):
            if button is not None:
                button.configure(state=tk.NORMAL)

    def build_args(self) -> argparse.Namespace:
        output_root = self.output_root.get().strip()
        if not output_root:
            raise SystemExit("Model Output Root is required.")
        return argparse.Namespace(
            output_root=output_root,
            base_url=self.base_url.get().strip(),
            vad_file=self.vad_file.get().strip(),
            vad_version=self.vad_version.get().strip() or "1.0.0",
            copy_vad_file=bool(self.copy_vad_file.get()),
            sherpa_path=self.sherpa_path.get().strip(),
            sherpa_version=self.sherpa_version.get().strip() or "2023-02-20",
            sherpa_dir_name=self.sherpa_dir_name.get().strip() or generator.DEFAULT_SHERPA_DIR_NAME,
            copy_sherpa_package=bool(self.copy_sherpa_package.get()),
        )

    def open_output_folder(self) -> None:
        target = self._resolve_current_folder()
        if not target:
            messagebox.showinfo("Open Current Folder", "No current folder is set yet.")
            return
        path = Path(target).expanduser().resolve()
        if not path.exists():
            messagebox.showwarning("Open Current Folder", f"Folder does not exist:\n{path}")
            return
        try:
            if os.name == "nt":
                os.startfile(str(path))
            else:
                subprocess.Popen(["xdg-open", str(path)])
        except Exception as exc:
            messagebox.showerror("Open Current Folder", str(exc))

    def log(self, message: str) -> None:
        if self.log_box is None:
            return
        self.log_box.configure(state=tk.NORMAL)
        self.log_box.insert(tk.END, message + "\n")
        self.log_box.see(tk.END)
        self.log_box.configure(state=tk.DISABLED)


def main() -> int:
    root = tk.Tk()
    style = ttk.Style()
    try:
        style.theme_use("vista")
    except tk.TclError:
        pass
    ReleasePublisherGui(root)
    root.mainloop()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
