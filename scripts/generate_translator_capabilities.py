#!/usr/bin/env python3
import argparse
import json
import os
import re
import sys
from pathlib import Path

import requests
from lxml import etree


WHISPER_LANGUAGES = [
    ("af", "Afrikaans"),
    ("am", "Amharic"),
    ("ar", "Arabic"),
    ("as", "Assamese"),
    ("az", "Azerbaijani"),
    ("ba", "Bashkir"),
    ("be", "Belarusian"),
    ("bg", "Bulgarian"),
    ("bn", "Bengali"),
    ("bo", "Tibetan"),
    ("br", "Breton"),
    ("bs", "Bosnian"),
    ("ca", "Catalan"),
    ("cs", "Czech"),
    ("cy", "Welsh"),
    ("da", "Danish"),
    ("de", "German"),
    ("el", "Greek"),
    ("en", "English"),
    ("es", "Spanish"),
    ("et", "Estonian"),
    ("eu", "Basque"),
    ("fa", "Persian"),
    ("fi", "Finnish"),
    ("fo", "Faroese"),
    ("fr", "French"),
    ("gl", "Galician"),
    ("gu", "Gujarati"),
    ("ha", "Hausa"),
    ("haw", "Hawaiian"),
    ("he", "Hebrew"),
    ("hi", "Hindi"),
    ("hr", "Croatian"),
    ("ht", "Haitian Creole"),
    ("hu", "Hungarian"),
    ("hy", "Armenian"),
    ("id", "Indonesian"),
    ("is", "Icelandic"),
    ("it", "Italian"),
    ("ja", "Japanese"),
    ("jw", "Javanese"),
    ("ka", "Georgian"),
    ("kk", "Kazakh"),
    ("km", "Khmer"),
    ("kn", "Kannada"),
    ("ko", "Korean"),
    ("la", "Latin"),
    ("lb", "Luxembourgish"),
    ("ln", "Lingala"),
    ("lo", "Lao"),
    ("lt", "Lithuanian"),
    ("lv", "Latvian"),
    ("mg", "Malagasy"),
    ("mi", "Maori"),
    ("mk", "Macedonian"),
    ("ml", "Malayalam"),
    ("mn", "Mongolian"),
    ("mr", "Marathi"),
    ("ms", "Malay"),
    ("mt", "Maltese"),
    ("my", "Myanmar"),
    ("ne", "Nepali"),
    ("nl", "Dutch"),
    ("nn", "Norwegian Nynorsk"),
    ("no", "Norwegian"),
    ("oc", "Occitan"),
    ("pa", "Punjabi"),
    ("pl", "Polish"),
    ("ps", "Pashto"),
    ("pt", "Portuguese"),
    ("ro", "Romanian"),
    ("ru", "Russian"),
    ("sa", "Sanskrit"),
    ("sd", "Sindhi"),
    ("si", "Sinhala"),
    ("sk", "Slovak"),
    ("sl", "Slovenian"),
    ("sn", "Shona"),
    ("so", "Somali"),
    ("sq", "Albanian"),
    ("sr", "Serbian"),
    ("su", "Sundanese"),
    ("sv", "Swedish"),
    ("sw", "Swahili"),
    ("ta", "Tamil"),
    ("te", "Telugu"),
    ("tg", "Tajik"),
    ("th", "Thai"),
    ("tk", "Turkmen"),
    ("tl", "Tagalog"),
    ("tr", "Turkish"),
    ("tt", "Tatar"),
    ("uk", "Ukrainian"),
    ("ur", "Urdu"),
    ("uz", "Uzbek"),
    ("vi", "Vietnamese"),
    ("yi", "Yiddish"),
    ("yo", "Yoruba"),
    ("yue", "Cantonese"),
    ("zh", "Chinese"),
]


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Generate translator runtime capabilities metadata.")
    parser.add_argument("--project-root", required=True, help="Repository root path.")
    parser.add_argument("--output", required=True, help="Output JSON path.")
    return parser.parse_args()


def resolve_runtime_root(project_root: Path, output_path: Path) -> Path:
    candidates = [
        output_path.parent,
        project_root / "tools" / "translator",
        project_root / "translator",
    ]
    for candidate in candidates:
        if (candidate / "_internal").exists():
            return candidate
    return output_path.parent


def load_translator_engines(project_root: Path, runtime_root: Path) -> list[str]:
    server_paths = [
        runtime_root / "_internal" / "translators" / "server.py",
        project_root / "ref" / "translators" / "translators" / "server.py",
    ]

    for server_path in server_paths:
        if not server_path.exists():
            continue

        text = server_path.read_text(encoding="utf-8")
        match = re.search(r"self\.translators_dict\s*=\s*\{(?P<body>.*?)\n\s*\}", text, re.S)
        if not match:
            continue

        keys = re.findall(r"'([^']+)'\s*:\s*self\.", match.group("body"))
        if keys:
            return keys

    raise RuntimeError("Failed to parse translators_dict from runtime server.py")


def load_translation_languages(project_root: Path, runtime_root: Path) -> list[dict[str, str]]:
    bing_items = load_bing_languages_from_bing_site()
    if bing_items:
        return bing_items

    metadata_items = load_bing_languages_from_metadata(runtime_root)
    if metadata_items:
        return metadata_items

    readme_items = load_bing_languages_from_readme(project_root)
    if readme_items:
        return readme_items

    raise RuntimeError("No Bing language catalog could be loaded from runtime metadata or README")


def load_bing_languages_from_bing_site() -> list[dict[str, str]]:
    urls = [
        "https://www.bing.com/Translator",
        "https://cn.bing.com/Translator",
    ]
    option_xpaths = [
        '//*[@id="tta_srcsl"]/option',
        '//*[@id="t_srcAllLang"]/option',
    ]

    for url in urls:
        try:
            response = requests.get(url, timeout=20)
            response.raise_for_status()
            document = etree.HTML(response.text)

            for xpath in option_xpaths:
                options = document.xpath(xpath)
                if not options:
                    continue

                items: list[dict[str, str]] = []
                seen: set[str] = set()
                for option in options:
                    code = (option.get("value") or "").strip().lower()
                    display_name = "".join(option.itertext()).strip()
                    if not code or code in {"auto-detect", "auto"} or code in seen:
                        continue

                    seen.add(code)
                    items.append(
                        {
                            "code": code,
                            "displayName": display_name or code,
                        }
                    )

                if items:
                    return items
        except Exception:
            continue

    return []


def load_bing_languages_from_metadata(runtime_root: Path) -> list[dict[str, str]]:
    metadata_files = sorted(runtime_root.glob("_internal/translators-*.dist-info/METADATA"))
    for metadata_path in metadata_files:
        items = parse_bing_languages_from_markdown(metadata_path.read_text(encoding="utf-8"))
        if items:
            return items
    return []


def load_bing_languages_from_readme(project_root: Path) -> list[dict[str, str]]:
    readme_path = project_root / "ref" / "translators" / "README.md"
    if not readme_path.exists():
        return []
    return parse_bing_languages_from_markdown(readme_path.read_text(encoding="utf-8"))


def parse_bing_languages_from_markdown(text: str) -> list[dict[str, str]]:
    items: list[dict[str, str]] = []
    seen: set[str] = set()
    bing_column_index: int | None = None
    in_section = False

    for raw_line in text.splitlines():
        line = raw_line.strip()
        if line.startswith("| Language") and "[Bing](https://www.bing.com/Translator)" in line:
            headers = [cell.strip() for cell in line.strip("|").split("|")]
            for index, header in enumerate(headers):
                if "[Bing](https://www.bing.com/Translator)" in header:
                    bing_column_index = index
                    break
            in_section = bing_column_index is not None
            continue

        if not in_section:
            continue

        if not line.startswith("|"):
            if items:
                break
            continue

        cells = [cell.strip() for cell in line.strip("|").split("|")]
        if len(cells) <= 1 or bing_column_index is None or len(cells) <= bing_column_index:
            continue

        language_name = cells[0]
        code = cells[1].strip().lower()
        bing_support = cells[bing_column_index].strip()

        if language_name.lower() == "language" or re.fullmatch(r"-{3,}", language_name.replace(" ", "")):
            continue
        if code in {"...", ""}:
            continue
        if not bing_support.startswith("Y"):
            continue
        if code in seen:
            continue

        seen.add(code)
        items.append(
            {
                "code": code,
                "displayName": language_name,
            }
        )

    return items


def load_recognition_languages() -> list[dict[str, str]]:
    return [{"code": code, "displayName": name} for code, name in WHISPER_LANGUAGES]


def main() -> int:
    args = parse_args()
    project_root = Path(args.project_root).resolve()
    output_path = Path(args.output).resolve()
    runtime_root = resolve_runtime_root(project_root, output_path)

    output_path.parent.mkdir(parents=True, exist_ok=True)

    payload = {
        "schemaVersion": 1,
        "supportedRecognitionLanguages": load_recognition_languages(),
        "supportedTranslationLanguages": load_translation_languages(project_root, runtime_root),
        "supportedTranslationEngines": load_translator_engines(project_root, runtime_root),
    }

    output_path.write_text(json.dumps(payload, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"Wrote translator capabilities: {output_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
