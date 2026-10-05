"""Download the optional research-only Stenoz XCA checkpoints.

Weights are kept outside git under ai-research/models/.
"""
from __future__ import annotations

from pathlib import Path
import shutil

from huggingface_hub import hf_hub_download

ROOT = Path(__file__).resolve().parent
TARGET_DIR = ROOT / "models"
REPO_ID = "uzbtrust/stenoz-coronary-stenosis-unet"
FILES = ("unet_stenosis.pt", "unet_vessel.pt")


def main() -> None:
    TARGET_DIR.mkdir(parents=True, exist_ok=True)

    for filename in FILES:
        target = TARGET_DIR / filename
        if target.exists():
            print(f"Already installed: {target}")
            continue

        print(f"Downloading research-only checkpoint: {filename} ...")
        cached = Path(hf_hub_download(repo_id=REPO_ID, filename=filename))
        shutil.copy2(cached, target)
        print(f"Installed: {target}")

    print()
    print("IMPORTANT: These models are for research/demonstration only.")
    print("They are not clinically validated or certified and have known false positives.")
    print("Any narrowing percentage shown by Cardio Guardian is a wide-range research estimate,")
    print("not clinical QCA, and a negative result must not be interpreted as no stenosis.")


if __name__ == "__main__":
    main()
