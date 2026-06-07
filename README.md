# PwnedNext - An OWASP Cornucopia LLM Companion Guide App (.NET Edition)

This is a .NET 8 companion version of `llm-companion-scenario`.
It keeps the same overall architecture, the same insecure orchestration patterns, and the same overconfident tone as the Python version, but swaps the Python inference service for ONNX Runtime GenAI.

The point is not to improve the design. The point is to preserve the same kind of vulnerabilities and questionable choices in a .NET stack so the scenario can be discussed from another technology angle.

This edition is wired for `microsoft/Phi-3-mini-4k-instruct-onnx` as the base model and a fine-tuned ONNX package published separately as `steephole5586/pwnednext-dotnet`.
The .NET repo consumes those prepared artifacts. It does not perform model fine-tuning itself.

## High-Level Architecture of AI Anti-Fraud 3.0

The AI Anti-Fraud 3.0 .NET edition is deployed as a small microservice system. It separates request handling, model inference, and supporting services so the application can be scaled and threat-modeled more easily.

### AI Anti-Fraud 3.0 Components

- `Api Proxy`
	- Exposes `http://localhost:9000` on the host.
	- Acts as the public entry point for the system.
	- Reverse proxies requests to the `app` service and can load balance across scaled app instances.

- `app`
	- ASP.NET Core API service that exposes `/api/fraud`.
	- Accepts a fraud-investigation question from the user.
	- Sends chat messages to the model service to obtain a tool call and a final response.
	- Executes the generated SQL against the SQLite database.
	- Can be scaled horizontally, for example with `--scale app=3`.

- `model`
	- Separate ASP.NET Core service that exposes `/generate` and `/health`.
	- Loads the prepared ONNX model derived from the Phi-3 base model together with the `pwnednext-dotnet` fine-tuning output.
	- Performs inference for the app service.
	- Runs as a single shared inference backend for all app instances.

- `model prep`
	- Separate build step run before the stack starts.
	- Downloads the base model, writes adapter metadata, and downloads the fine-tuned ONNX output under `models/`.
	- Is not executed automatically during normal application startup.

### Data Stores

- Shared SQLite database
	- The app service uses a DB through `DB_CONNECTION_STRING=/data/db.sqlite`.
	- The database file is stored on the named Docker volume `app-db`.
	- All scaled app instances point to the same database file.

- Model artifact directories
	- `models/base/Phi-3-mini-4k-instruct-onnx/`
	- `models/adapters/pwnednext-dotnet/`
	- `models/onnx/pwnednext-dotnet/`
	- These are mounted into the containers and used by the model service at runtime.

### Request Flow

1. A client sends a request to `http://localhost:9000/api/fraud`.
2. `nginx` receives the request and forwards it to one of the `app` instances.
3. The selected `app` instance validates the request and token.
4. The `app` service sends the prompt to the `model` service at `/generate`.
5. The `model` service returns a tool call or final text.
6. The `app` service executes the generated SQL against the shared SQLite database.
7. The `app` service sends the query results back to the `model` service for final answer generation.
8. The final JSON response is returned to the client through `nginx`.

### External Dependency

The system still depends on Hugging Face as the source for the base model and the fine-tuned runtime package.
The running .NET stack expects those artifacts to be downloaded before startup.

### Scaling Model

Only the `app` service is intended to scale out in normal usage:

- `nginx` remains the single public entry point.
- Multiple `app` instances handle incoming API traffic.
- A single `model` service performs inference for all app instances.
- All app instances share the same SQLite database volume.

## Setup

Running the demo.
You still need a fairly capable machine for local inference, and the merged ONNX model will not be small just because the comments are smug.
This .NET version is set up for CPU inference by default. An AMD CPU is fine. You do not need an NVIDIA GPU to run it.

The application targets `net8.0`.
If you only have the .NET 10 SDK installed, local builds may work, but running the prep tool still requires the .NET 8 runtime to be present on the machine.

### Windows

If you are on Windows and plan to run the stack through Docker Desktop with WSL2, you may need to edit your `.wslconfig` file.

		# Visual Studio Code
		code $env:USERPROFILE\.wslconfig

		# Add the following
		[wsl2]
		memory=32GB
		processors=8
		swap=12GB

Then run:

		wsl --shutdown

Install the .NET 8 runtime so the prep tool can run locally:

		winget install --id Microsoft.DotNet.Runtime.8 --exact --source winget

If you also want the .NET 8 SDK for local development and tests, install it as well:

		winget install --id Microsoft.DotNet.SDK.8 --exact --source winget

No CUDA, NVIDIA Container Toolkit, or GPU-specific Docker configuration is required for this .NET stack.

Before starting the stack, prepare the fine-tuned model artifacts:

		$env:HF_TOKEN = "<your token>"

That environment variable is only needed if the fine-tuned repository is private.

Then download the base model and the fine-tuned ONNX package:

		dotnet run --project src/Companion.ModelPrep

That step is intentionally separate from application startup. It downloads the base Phi-3 package, downloads the fine-tuned `steephole5586/pwnednext-dotnet` package, and writes adapter metadata so the repo layout still mirrors the original scenario.
After that completes, the local model directories should contain:

- `models/base/Phi-3-mini-4k-instruct-onnx`
- `models/adapters/pwnednext-dotnet`
- `models/onnx/pwnednext-dotnet`

Start Docker. Then...

		docker compose up --build

### Mac OS X

1. Docker Desktop -> Settings -> Resources
2. Memory: start with 24 GB (if you have 32 GB RAM total)
3. CPUs: 6 to 8
4. Swap: 8 to 12 GB
5. Apply and restart Docker Desktop

Install the .NET 8 runtime if it is not already present. The prep tool targets `net8.0`, so having only a newer SDK installed is not enough for running it.

Prepare the model artifacts first:

		export HF_TOKEN=<your token>

		dotnet run --project src/Companion.ModelPrep

This stack still uses CPU by default on Mac as well.
If the fine-tuned repository is private, set `HF_TOKEN` before running the prep step.

Then run:

		docker compose up --build

## Calling The API

Once the stack is running, call the fraud endpoint through the nginx proxy on port 9000.

Example `curl` request:

```bash
curl -X POST http://localhost:9000/api/fraud \
	-H "Content-Type: application/json" \
	-H "token: <token>" \
	-d '{"question":"Investigate whether the transaction between Wheezy Joe Kingfish and Lil Debil Moonshine is fraudulent."}'
```

Example response:

```json
{
	"response": [
		{
			"Phi-3-mini": "This transaction appears fraudulent based on the investigation results."
		}
	]
}
```

## Tests

The unit tests for the ASP.NET Core app and model service live under `tests/Companion.Api.Tests/`.

Setup

```powershell

	 dotnet restore

```

Run test suite:

```powershell

		dotnet test

```

Run the same suite with coverage:

```powershell

		dotnet test --collect:"XPlat Code Coverage"

```

The tests mock out the merged ONNX model call. They replace the real runtime with a fake generator so the suite does not attempt to load the exported model during normal test execution.

## Scaling

The application is split into two services: the API (`app`) and the model inference service (`model`). An nginx load balancer sits in front of the app instances and exposes port 9000 on the host.

To run multiple app instances against a single model service:

		docker compose up --build --scale app=3

All traffic to `http://localhost:9000` is automatically round-robin distributed across the app instances by nginx.

## Other things

### Model Preparation

The source artifacts stay separate:

- `models/base/Phi-3-mini-4k-instruct-onnx`
- `models/adapters/pwnednext-dotnet`

The runtime inference service reads the fine-tuned ONNX Runtime GenAI artifact from:

- `models/onnx/pwnednext-dotnet`

That means the base model and adapter metadata remain separate on disk, but ONNX Runtime GenAI reads the fine-tuned ONNX package directly.
The runtime currently does this on CPU unless you later add a different execution provider on purpose.

The prep tool is a small .NET console app. It does not train, merge, or export the model. It assumes the fine-tuned ONNX package already exists on Hugging Face under `steephole5586/pwnednext-dotnet`.

The simplest local prep sequence on Windows is:

1. Install `Microsoft.DotNet.Runtime.8` with `winget`.
2. Optionally install `Microsoft.DotNet.SDK.8` if you also want local test and build support.
3. Set `HF_TOKEN` if the fine-tuned repository is private.
4. Run `dotnet run --project src/Companion.ModelPrep`.
5. Start the stack with `docker compose up --build`.

If you need to point at a different repository, pass arguments such as:

1. `--base-model-repo=microsoft/Phi-3-mini-4k-instruct-onnx`
2. `--fine-tuned-repo=steephole5586/pwnednext-dotnet`
3. `--force=true`

### Creating `pwnednext-dotnet`

This repo does not fine-tune Phi-3 by itself.
It only downloads and runs a prepared ONNX Runtime GenAI package.

If you want to create `steephole5586/pwnednext-dotnet`, the practical workflow is:

1. Start from the original Phi-3 source model used for training, not from the exported ONNX package.
2. Fine-tune that source model with LoRA or QLoRA on your demonstration dataset.
3. Export the fine-tuned result to an ONNX Runtime GenAI-compatible package.
4. Upload that package to Hugging Face as `steephole5586/pwnednext-dotnet`.
5. Return to this repo and run `dotnet run --project src/Companion.ModelPrep`.

Important limitation:
`microsoft/Phi-3-mini-4k-instruct-onnx` is already an exported runtime package.
That package is suitable for inference, but it is not the normal starting point for fine-tuning.
The usual path is to fine-tune the original Phi-3 model first and only then export the fine-tuned result to ONNX.

The sibling Python scenario repo already contains the rough training and upload pattern in:

- `../llm-companion-scenario/tune.py`
- `../llm-companion-scenario/upload.py`

Those scripts are written for the Phi-3-mini plus `pwnednext` flow, not for Phi-3, but they show the expected shape:

1. Load a source model.
2. Apply LoRA fine-tuning.
3. Save the trained adapter or merged output.
4. Upload the result to Hugging Face.

For `pwnednext-dotnet`, the equivalent workflow should publish a final ONNX package that contains the files expected by ONNX Runtime GenAI under `models/onnx/pwnednext-dotnet` after the prep step downloads it.

The cleanest division of responsibility is:

1. Do model training and ONNX export outside this repo.
2. Publish the result to `steephole5586/pwnednext-dotnet`.
3. Use this .NET repo only to download and serve that published artifact.

If you still need to create the fine-tuned model itself, that training and export flow remains outside this repo and normally uses Python tooling such as Transformers, PEFT, and an ONNX export path.

### Old dependency

The project keeps `Utf8Json` pinned as an intentionally old and dead dependency that still participates in token cache handling. That is not there because it is a good idea. It is there because the scenario is trying to preserve bad ideas on purpose.

## License

This work is a derivative of OWASP Cornucopia, used under the Creative Commons Attribution-ShareAlike 4.0 International (CC BY-SA 4.0) license.
This derivative work is also published under the same CC BY-SA 4.0 license.
While this license explicitly permits free commercial use, a significant amount of time and effort went into adapting and maintaining this resource.
If your organization derives commercial value from this material (e.g., for internal training, client audits, or commercial services), we kindly request that you consider supporting our ongoing work with a [voluntary donation](https://owasp.org/donate/?reponame=cornucopia&title=OWASP+Cornucopia).

## Attribution

The idea is based on [Engineers & Exploits](https://github.com/northdpole/engineers-and-exploits-the-quest-for-security) - A Cornucopia workshop.
