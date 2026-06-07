import argparse
import inspect
import importlib.util
import json
import locale
import os
import shutil
import subprocess
import sys
from pathlib import Path

import torch
from datasets import load_dataset
from huggingface_hub import snapshot_download
from peft import LoraConfig, prepare_model_for_kbit_training
from transformers import AutoConfig, AutoModelForCausalLM, AutoTokenizer, BitsAndBytesConfig


DEFAULT_TARGET_MODULES = [
    "q_proj",
    "k_proj",
    "v_proj",
    "o_proj",
    "gate_proj",
    "up_proj",
    "down_proj",
]


def force_utf8_defaults() -> None:
    os.environ.setdefault("PYTHONUTF8", "1")
    os.environ.setdefault("PYTHONIOENCODING", "utf-8")

    original_read_text = Path.read_text

    def read_text_utf8(self: Path, encoding=None, errors=None):
        return original_read_text(self, encoding=encoding or "utf-8", errors=errors)

    Path.read_text = read_text_utf8

    def getpreferredencoding(do_setlocale: bool = True) -> str:
        return "utf-8"

    locale.getpreferredencoding = getpreferredencoding

    if hasattr(locale, "getencoding"):
        locale.getencoding = lambda: "utf-8"


def import_trl_types():
    force_utf8_defaults()
    from trl import SFTConfig, SFTTrainer

    return SFTConfig, SFTTrainer


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Fine-tune Phi-3 and optionally export an ONNX Runtime GenAI package for pwnednext-dotnet.")
    parser.add_argument("--base-model", default="microsoft/Phi-3-mini-4k-instruct")
    parser.add_argument("--dataset-name", default="timdettmers/openassistant-guanaco")
    parser.add_argument("--dataset-split", default="train[:1000]")
    parser.add_argument("--dataset-path", help="Optional local JSON or JSONL file instead of a Hugging Face dataset.")
    parser.add_argument("--dataset-text-field", default="text")
    parser.add_argument("--adapter-output", default="./artifacts/pwnednext-dotnet-adapter")
    parser.add_argument("--onnx-output", default="./artifacts/pwnednext-dotnet-onnx")
    parser.add_argument("--cache-dir", default="./artifacts/onnx-build-cache")
    parser.add_argument("--per-device-train-batch-size", type=int, default=2)
    parser.add_argument("--gradient-accumulation-steps", type=int, default=4)
    parser.add_argument("--learning-rate", type=float, default=2e-4)
    parser.add_argument("--logging-steps", type=int, default=10)
    parser.add_argument("--save-steps", type=int, default=50)
    parser.add_argument("--max-steps", type=int, default=100)
    parser.add_argument("--max-length", type=int, default=512)
    parser.add_argument("--lora-r", type=int, default=16)
    parser.add_argument("--lora-alpha", type=int, default=32)
    parser.add_argument("--lora-dropout", type=float, default=0.05)
    parser.add_argument("--compute-dtype", choices=["float16", "float32", "bfloat16"], default="float32")
    parser.add_argument("--precision", default="int4", help="Quantization mode passed to onnxruntime_genai.models.builder.")
    parser.add_argument("--execution-provider", default="cpu", help="Execution provider passed to the ONNX builder.")
    parser.add_argument("--no-4bit", action="store_true", help="Disable QLoRA-style 4-bit loading.")
    parser.add_argument("--export-only", action="store_true", help="Skip fine-tuning and export an ONNX package from an existing saved adapter.")
    parser.add_argument("--skip-export", action="store_true", help="Train and save the adapter, but skip the ONNX export step.")
    parser.add_argument(
        "--target-modules",
        nargs="+",
        default=DEFAULT_TARGET_MODULES,
        help="LoRA target modules for the Phi-3 source model.")
    return parser.parse_args()


def resolve_torch_dtype(dtype_name: str) -> torch.dtype:
    if dtype_name == "float16":
        return torch.float16
    if dtype_name == "bfloat16":
        return torch.bfloat16
    return torch.float32


def load_model_config(base_model: str):
    config = AutoConfig.from_pretrained(base_model, trust_remote_code=True)
    rope_scaling = getattr(config, "rope_scaling", None)

    if isinstance(rope_scaling, dict):
        normalized_rope_scaling = dict(rope_scaling)
        rope_type = normalized_rope_scaling.get("type") or normalized_rope_scaling.get("rope_type")
        if rope_type in {None, "default"}:
            config.rope_scaling = None
        else:
            normalized_rope_scaling["type"] = rope_type
            normalized_rope_scaling.pop("rope_type", None)
            normalized_rope_scaling.pop("rope_theta", None)
            config.rope_scaling = normalized_rope_scaling or None

    config._attn_implementation = "eager"
    return config


def load_training_dataset(args: argparse.Namespace):
    if args.dataset_path:
        dataset_path = Path(args.dataset_path)
        if not dataset_path.exists():
            raise FileNotFoundError(f"Dataset file was not found: {dataset_path}")

        suffix = dataset_path.suffix.lower()
        if suffix not in {".json", ".jsonl"}:
            raise ValueError("dataset-path must point to a .json or .jsonl file.")

        return load_dataset("json", data_files=str(dataset_path), split="train")

    return load_dataset(args.dataset_name, split=args.dataset_split)


def normalize_rope_scaling(config_data: dict) -> dict:
    normalized_config = dict(config_data)
    rope_scaling = normalized_config.get("rope_scaling")

    if isinstance(rope_scaling, dict):
        normalized_rope_scaling = dict(rope_scaling)
        rope_type = normalized_rope_scaling.get("type") or normalized_rope_scaling.get("rope_type")
        if rope_type in {None, "default"}:
            normalized_config["rope_scaling"] = None
        else:
            normalized_rope_scaling["type"] = rope_type
            normalized_rope_scaling.pop("rope_type", None)
            normalized_rope_scaling.pop("rope_theta", None)
            normalized_config["rope_scaling"] = normalized_rope_scaling or None

    return normalized_config


def patch_export_phi3_configuration(source_text: str) -> str:
    original = """        super().__init__(\n            bos_token_id=bos_token_id,\n            eos_token_id=eos_token_id,\n            pad_token_id=pad_token_id,\n            tie_word_embeddings=tie_word_embeddings,\n            **kwargs,\n        )\n"""
    replacement = """        super().__init__(\n            bos_token_id=bos_token_id,\n            eos_token_id=eos_token_id,\n            pad_token_id=pad_token_id,\n            tie_word_embeddings=tie_word_embeddings,\n            **kwargs,\n        )\n\n        rope_scaling = getattr(self, \"rope_scaling\", None)\n        if isinstance(rope_scaling, dict):\n            rope_type = rope_scaling.get(\"type\") or rope_scaling.get(\"rope_type\")\n            if rope_type in {None, \"default\"}:\n                self.rope_scaling = None\n            elif \"type\" not in rope_scaling:\n                rope_scaling[\"type\"] = rope_type\n                rope_scaling.pop(\"rope_type\", None)\n                rope_scaling.pop(\"rope_theta\", None)\n                self.rope_scaling = rope_scaling\n"""

    if original not in source_text:
        raise ValueError("Unable to patch Phi-3 configuration source for export.")

    return source_text.replace(original, replacement, 1)


def link_or_copy_file(source: Path, destination: Path) -> None:
    if destination.exists():
        return

    try:
        os.link(source, destination)
    except OSError:
        shutil.copy2(source, destination)


def prepare_export_model_source(base_model: str, cache_dir: Path) -> Path:
    base_model_path = Path(base_model).expanduser()
    source_root = base_model_path if base_model_path.exists() else Path(snapshot_download(repo_id=base_model))
    export_source = cache_dir / "export-model-source"
    export_source.mkdir(parents=True, exist_ok=True)

    for source_path in source_root.rglob("*"):
        relative_path = source_path.relative_to(source_root)
        destination_path = export_source / relative_path

        if source_path.is_dir():
            destination_path.mkdir(parents=True, exist_ok=True)
            continue

        destination_path.parent.mkdir(parents=True, exist_ok=True)
        if relative_path.name == "config.json":
            config_data = json.loads(source_path.read_text(encoding="utf-8"))
            normalized_config = normalize_rope_scaling(config_data)
            normalized_config.setdefault("_attn_implementation", "eager")
            destination_path.write_text(json.dumps(normalized_config, indent=2) + "\n", encoding="utf-8")
            continue

        if relative_path.name == "configuration_phi3.py":
            destination_path.write_text(
                patch_export_phi3_configuration(source_path.read_text(encoding="utf-8")),
                encoding="utf-8",
            )
            continue

        link_or_copy_file(source_path, destination_path)

    return export_source


def save_manifest(args: argparse.Namespace, adapter_output: Path, onnx_output: Path) -> None:
    manifest = {
        "base_model": args.base_model,
        "dataset_name": args.dataset_name,
        "dataset_split": args.dataset_split,
        "dataset_path": args.dataset_path,
        "dataset_text_field": args.dataset_text_field,
        "adapter_output": str(adapter_output),
        "onnx_output": str(onnx_output),
        "precision": args.precision,
        "execution_provider": args.execution_provider,
        "max_steps": args.max_steps,
        "lora": {
            "r": args.lora_r,
            "alpha": args.lora_alpha,
            "dropout": args.lora_dropout,
            "target_modules": args.target_modules,
        },
    }
    (adapter_output / "training-manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")


def build_export_command(args: argparse.Namespace, adapter_output: Path, onnx_output: Path, cache_dir: Path, model_source: Path | None = None) -> list[str]:
    command = [
        sys.executable,
        "-m",
        "onnxruntime_genai.models.builder",
        "-o",
        str(onnx_output),
        "-p",
        args.precision,
        "-e",
        args.execution_provider,
        "-c",
        str(cache_dir),
        "--extra_options",
        f"adapter_path={adapter_output}",
    ]

    if model_source is not None:
        command[3:3] = ["-i", str(model_source)]
    elif Path(args.base_model).expanduser().exists():
        command[3:3] = ["-i", str(Path(args.base_model).expanduser())]
    else:
        command[3:3] = ["-m", args.base_model]

    return command


def ensure_export_dependencies() -> None:
    if importlib.util.find_spec("onnx_ir") is None:
        raise ModuleNotFoundError(
            "The ONNX Runtime GenAI builder requires the 'onnx-ir' package. "
            "Install the helper dependencies again with 'pip install -r requirements.txt'."
        )


def ensure_adapter_artifacts(adapter_output: Path) -> None:
    required_files = [
        adapter_output / "adapter_config.json",
        adapter_output / "adapter_model.safetensors",
    ]

    missing_files = [str(path) for path in required_files if not path.exists()]
    if missing_files:
        raise FileNotFoundError(
            "Export-only mode requires an existing saved adapter. Missing files: " + ", ".join(missing_files)
        )


def main() -> None:
    args = parse_args()
    SFTConfig, SFTTrainer = import_trl_types()

    adapter_output = Path(args.adapter_output)
    onnx_output = Path(args.onnx_output)
    cache_dir = Path(args.cache_dir)

    adapter_output.mkdir(parents=True, exist_ok=True)
    onnx_output.mkdir(parents=True, exist_ok=True)
    cache_dir.mkdir(parents=True, exist_ok=True)

    if args.export_only:
        ensure_adapter_artifacts(adapter_output)

        if args.skip_export:
            print("Skipping ONNX export because --skip-export was requested.")
            print("Existing adapter artifacts are ready.")
            return

        ensure_export_dependencies()
        export_model_source = prepare_export_model_source(args.base_model, cache_dir)
        export_command = build_export_command(args, adapter_output, onnx_output, cache_dir, model_source=export_model_source)
        print(f"Using existing LoRA adapter from {adapter_output}...")
        print("Exporting fine-tuned Phi-3 model to an ONNX Runtime GenAI package...")
        print(" ".join(export_command))
        subprocess.run(export_command, check=True)
        print(f"ONNX package created at {onnx_output}")
        print("Export complete.")
        return

    print(f"Loading tokenizer from {args.base_model}...")
    tokenizer = AutoTokenizer.from_pretrained(args.base_model, trust_remote_code=True)
    if tokenizer.pad_token is None:
        tokenizer.pad_token = tokenizer.eos_token

    config = load_model_config(args.base_model)

    quantization_config = None
    if not args.no_4bit:
        quantization_config = BitsAndBytesConfig(
            load_in_4bit=True,
            bnb_4bit_quant_type="nf4",
            bnb_4bit_compute_dtype=resolve_torch_dtype(args.compute_dtype),
            bnb_4bit_use_double_quant=True,
        )

    print(f"Loading Phi-3 source model from {args.base_model}...")
    model = AutoModelForCausalLM.from_pretrained(
        args.base_model,
        config=config,
        trust_remote_code=True,
        quantization_config=quantization_config,
        device_map="auto",
    )

    if quantization_config is not None:
        model = prepare_model_for_kbit_training(model)

    lora_config = LoraConfig(
        r=args.lora_r,
        lora_alpha=args.lora_alpha,
        target_modules=args.target_modules,
        lora_dropout=args.lora_dropout,
        bias="none",
        task_type="CAUSAL_LM",
    )

    dataset = load_training_dataset(args)

    training_args = SFTConfig(
        output_dir=str(adapter_output),
        per_device_train_batch_size=args.per_device_train_batch_size,
        gradient_accumulation_steps=args.gradient_accumulation_steps,
        learning_rate=args.learning_rate,
        logging_steps=args.logging_steps,
        max_steps=args.max_steps,
        save_strategy="steps",
        save_steps=args.save_steps,
        dataset_text_field=args.dataset_text_field,
        max_length=args.max_length,
        bf16=args.compute_dtype == "bfloat16",
        fp16=args.compute_dtype == "float16",
        optim="adamw_torch",
        report_to="none",
    )

    trainer_kwargs = {
        "model": model,
        "train_dataset": dataset,
        "peft_config": lora_config,
        "args": training_args,
    }

    sft_init_params = inspect.signature(SFTTrainer.__init__).parameters
    if "processing_class" in sft_init_params:
        trainer_kwargs["processing_class"] = tokenizer
    elif "tokenizer" in sft_init_params:
        trainer_kwargs["tokenizer"] = tokenizer

    trainer = SFTTrainer(**trainer_kwargs)

    print("Starting Phi-3 fine-tuning...")
    trainer.train()

    print(f"Saving LoRA adapter to {adapter_output}...")
    trainer.model.save_pretrained(adapter_output)
    tokenizer.save_pretrained(adapter_output)
    save_manifest(args, adapter_output, onnx_output)

    if args.skip_export:
        print("Skipping ONNX export because --skip-export was requested.")
        print("Adapter training complete.")
        return

    ensure_export_dependencies()
    export_model_source = prepare_export_model_source(args.base_model, cache_dir)
    export_command = build_export_command(args, adapter_output, onnx_output, cache_dir, model_source=export_model_source)
    print("Exporting fine-tuned Phi-3 model to an ONNX Runtime GenAI package...")
    print(" ".join(export_command))
    subprocess.run(export_command, check=True)
    print(f"ONNX package created at {onnx_output}")
    print("Training and export complete.")


if __name__ == "__main__":
    main()