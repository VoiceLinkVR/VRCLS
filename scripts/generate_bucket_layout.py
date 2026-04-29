#!/usr/bin/env python3
from __future__ import annotations

import argparse
import hashlib
import json
import shutil
import sys
import zipfile
from pathlib import Path
from urllib.parse import urljoin


DEFAULT_SHERPA_DIR_NAME = "sherpa-onnx-streaming-zipformer-bilingual-zh-en-2023-02-20"
DEFAULT_VAD_FILE_NAME = "silero_vad.onnx"
DEFAULT_BASE_URL = "https://cloudflarestorage.boyqiu001.top/"


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Generate a static bucket layout for VRCLS model/resource updates."
    )

    parser.add_argument("--output-root", default="bucket-generated", help="Output bucket root directory.")
    parser.add_argument("--base-url", default=DEFAULT_BASE_URL, help="Bucket base URL.")

    parser.add_argument("--vad-file", default="", help="Path to silero_vad.onnx.")
    parser.add_argument("--vad-version", default="1.0.0", help="VAD model version.")
    parser.add_argument("--copy-vad-file", action="store_true", help="Copy VAD file into output models directory.")

    parser.add_argument(
        "--sherpa-path",
        default="",
        help="Path to sherpa model directory or zip package. A directory will be zipped automatically.",
    )
    parser.add_argument("--sherpa-version", default="2023-02-20", help="Sherpa model version.")
    parser.add_argument(
        "--sherpa-dir-name",
        default=DEFAULT_SHERPA_DIR_NAME,
        help="Installed folder name for sherpa model under pre-models.",
    )
    parser.add_argument(
        "--copy-sherpa-package",
        action="store_true",
        help="Copy or build sherpa package into output models directory.",
    )

    return parser.parse_args()


def normalize_base_url(base_url: str) -> str:
    base_url = (base_url or "").strip()
    if not base_url:
        return ""
    if not base_url.endswith("/"):
        base_url += "/"
    return base_url


def to_abs(path_text: str) -> Path:
    return Path(path_text).expanduser().resolve()


def sha256_of_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def write_json(path: Path, payload: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    text = json.dumps(payload, ensure_ascii=False, indent=2) + "\n"
    path.write_text(text, encoding="utf-8", newline="\n")


def maybe_copy(src: Path, dst: Path, enabled: bool) -> Path:
    if not enabled:
        return src

    dst.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(src, dst)
    return dst


def build_zip_from_directory(source_dir: Path, zip_path: Path) -> Path:
    zip_path.parent.mkdir(parents=True, exist_ok=True)
    if zip_path.exists():
        zip_path.unlink()

    with zipfile.ZipFile(zip_path, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for file_path in sorted(source_dir.rglob("*")):
            if not file_path.is_file():
                continue
            relative = file_path.relative_to(source_dir.parent)
            archive.write(file_path, relative.as_posix())

    return zip_path


def relative_or_absolute_url(base_url: str, relative_path: str) -> str:
    normalized_relative = relative_path.replace("\\", "/").lstrip("/")
    if not base_url:
        return normalized_relative
    return urljoin(base_url, normalized_relative)


def prepare_vad_entry(args: argparse.Namespace, output_root: Path, base_url: str) -> dict | None:
    if not args.vad_file:
        return None

    vad_src = to_abs(args.vad_file)
    if not vad_src.is_file():
        raise SystemExit(f"VAD file not found: {vad_src}")

    model_dst = output_root / "models" / DEFAULT_VAD_FILE_NAME
    vad_file = maybe_copy(vad_src, model_dst, args.copy_vad_file)
    relative_model_path = f"models/{vad_file.name}"

    return {
        "name": "silero-vad-v1",
        "type": "vad",
        "version": args.vad_version,
        "downloadUrl": relative_or_absolute_url(base_url, relative_model_path),
        "fileSize": vad_file.stat().st_size,
        "checksum": f"sha256:{sha256_of_file(vad_file)}",
        "installPath": DEFAULT_VAD_FILE_NAME,
        "archiveRoot": "",
        "required": True,
    }


def prepare_sherpa_entry(args: argparse.Namespace, output_root: Path, base_url: str) -> dict | None:
    if not args.sherpa_path:
        return None

    sherpa_src = to_abs(args.sherpa_path)
    package_name = f"{args.sherpa_dir_name}.zip"
    package_dst = output_root / "models" / package_name
    temp_package = output_root / ".tmp" / package_name

    if sherpa_src.is_dir():
        sherpa_package = build_zip_from_directory(
            sherpa_src,
            package_dst if args.copy_sherpa_package else temp_package,
        )
    elif sherpa_src.is_file():
        if sherpa_src.suffix.lower() != ".zip":
            raise SystemExit("Sherpa path must be a directory or a .zip file.")
        sherpa_package = maybe_copy(sherpa_src, package_dst, args.copy_sherpa_package)
    else:
        raise SystemExit(f"Sherpa path not found: {sherpa_src}")

    relative_model_path = f"models/{package_name if args.copy_sherpa_package else sherpa_package.name}"
    entry = {
        "name": "sherpa-zh-en-streaming",
        "type": "sherpa",
        "version": args.sherpa_version,
        "downloadUrl": relative_or_absolute_url(base_url, relative_model_path),
        "fileSize": sherpa_package.stat().st_size,
        "checksum": f"sha256:{sha256_of_file(sherpa_package)}",
        "installPath": args.sherpa_dir_name,
        "archiveRoot": args.sherpa_dir_name,
        "required": True,
    }

    if sherpa_package == temp_package:
        sherpa_package.unlink(missing_ok=True)
        try:
            sherpa_package.parent.rmdir()
        except OSError:
            pass

    return entry


def build_model_manifest(args: argparse.Namespace, output_root: Path, base_url: str) -> list[Path]:
    models: list[dict] = []

    vad_entry = prepare_vad_entry(args, output_root, base_url)
    if vad_entry:
        models.append(vad_entry)

    sherpa_entry = prepare_sherpa_entry(args, output_root, base_url)
    if sherpa_entry:
        models.append(sherpa_entry)

    if not models:
        return []

    manifest_path = output_root / "manifests" / "models" / "list.json"
    write_json(manifest_path, {"models": models})
    return [manifest_path]


def print_summary(output_root: Path, created: list[Path]) -> None:
    print("Generated VRCLS resource bucket layout:")
    print(f"  Root: {output_root}")
    for path in created:
        print(f"  {path}")


def generate_layout(args: argparse.Namespace) -> tuple[Path, list[Path]]:
    output_root = Path(args.output_root).expanduser().resolve()
    base_url = normalize_base_url(args.base_url)
    created = build_model_manifest(args, output_root, base_url)

    if not created:
        raise SystemExit("Nothing generated. Provide VAD and/or Sherpa resources.")

    return output_root, created


def main() -> int:
    args = parse_args()
    output_root, created = generate_layout(args)
    print_summary(output_root, created)
    return 0


if __name__ == "__main__":
    sys.exit(main())
