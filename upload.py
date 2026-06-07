import argparse
from pathlib import Path

from huggingface_hub import create_repo, upload_folder


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Upload the final ONNX Runtime GenAI package for pwnednext-dotnet to Hugging Face.")
    parser.add_argument("--repo-id", default="steephole5586/pwnednext-dotnet")
    parser.add_argument("--folder-path", default="./artifacts/pwnednext-dotnet-onnx")
    parser.add_argument("--commit-message", default="Upload fine-tuned Phi-3 ONNX Runtime GenAI package")
    parser.add_argument("--private", action="store_true")
    return parser.parse_args()


def validate_onnx_package(folder_path: Path) -> None:
    if not folder_path.exists():
        raise FileNotFoundError(f"Folder path does not exist: {folder_path}")

    genai_config = folder_path / "genai_config.json"
    onnx_files = list(folder_path.glob("*.onnx"))

    if not genai_config.exists():
        raise FileNotFoundError(
            "The ONNX package is missing genai_config.json at the package root. "
            "Upload the exported runtime package, not just the LoRA adapter.")

    if not onnx_files:
        raise FileNotFoundError(
            "The ONNX package does not contain any .onnx files at the package root. "
            "Upload the exported runtime package, not just the LoRA adapter.")


def ensure_model_card(folder_path: Path) -> None:
    readme_path = folder_path / "README.md"
    if readme_path.exists():
        return

    readme_path.write_text(
        """---
license: mit
pipeline_tag: text-generation
tags: [ONNX, ONNXRuntime, phi3, conversational]
---

# pwnednext-dotnet

This repository contains a fine-tuned ONNX Runtime GenAI package derived from Phi-3 for the `llm-companion-scenario-dotnet` demo.

- Base model: `microsoft/Phi-3-mini-4k-instruct`
- Runtime format: ONNX Runtime GenAI
- Intended consumer: `llm-companion-scenario-dotnet`

The repository is expected to contain `genai_config.json` and the exported `.onnx` model files at the package root.
""",
        encoding="utf-8",
    )


def main() -> None:
    args = parse_args()
    folder_path = Path(args.folder_path)

    validate_onnx_package(folder_path)
    ensure_model_card(folder_path)

    create_repo(repo_id=args.repo_id, repo_type="model", private=args.private, exist_ok=True)
    upload_folder(
        repo_id=args.repo_id,
        repo_type="model",
        folder_path=str(folder_path),
        commit_message=args.commit_message,
        ignore_patterns=["*.pt", "*.pth", "optimizer.pt", "scheduler.pt", "trainer_state.json"],
    )

    print(f"Uploaded ONNX package from {folder_path} to {args.repo_id}")


if __name__ == "__main__":
    main()