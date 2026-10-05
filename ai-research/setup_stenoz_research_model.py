"""Download the research-only Stenoz ARCADE XCA checkpoint.

Weights are kept outside git under ai-research/models/.
"""
from __future__ import annotations

from pathlib import Path
import shutil

from huggingface_hub import hf_hub_download

ROOT = Path(__file__).resolve().parent
TARGET_DIR = ROOT / "models"
TARGET = TARGET_DIR / "unet_stenosis.pt"
REPO_ID = "uzbtrust/stenoz-coronary-stenosis-unet"
FILENAME = "unet_stenosis.pt"


def main() -> None:
    TARGET_DIR.mkdir(parents=True, exist_ok=True)
    print("Downloading research-only XCA checkpoint...")
    cached = Path(hf_hub_download(repo_id=REPO_ID, filename=FILENAME))
    shutil.copy2(cached, TARGET)
    print(f"Installed: {TARGET}")
    print()
    print("IMPORTANT: This model is for research/demonstration only.")
    print("It is not clinically validated or certified and has known false positives.")
    print("A negative result must not be interpreted as no stenosis.")


if __name__ == "__main__":
    main()
