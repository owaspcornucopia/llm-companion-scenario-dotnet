# PwnedNext - An OWASP Cornucopia LLM Companion Guide App (.NET Edition)

This is a .NET 8 companion version of `llm-companion-scenario`.
It keeps the same overall architecture, the same insecure orchestration patterns, and the same overconfident tone as the Python version, but swaps the Python inference service for ONNX Runtime GenAI.

The point is not to improve the design. The point is to preserve the same kind of vulnerabilities and questionable choices in a .NET stack so the scenario can be discussed from another technology angle.

This edition is wired for `microsoft/Phi-3-mini-4k-instruct-onnx` as its runtime model.
The running stack uses the official CPU ONNX package directly.

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
	- Loads the official Phi-3 CPU ONNX model.
	- Performs inference for the app service.
	- Runs as a single shared inference backend for all app instances.

- `model prep`
	- Separate build step run before the stack starts.
	- Downloads the official base ONNX model under `models/`.
	- Is not executed automatically during normal application startup.

### Data Stores

- Shared SQLite database
	- The app service uses a DB through `DB_CONNECTION_STRING=/data/db.sqlite`.
	- The database file is stored on the named Docker volume `app-db`.
	- All scaled app instances point to the same database file.

- Model artifact directories
	- `models/base/Phi-3-mini-4k-instruct-onnx/`
	- The `cpu_and_mobile/cpu-int4-rtn-block-32/` variant is mounted into the model container at runtime.

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

The system depends on Hugging Face as the source for the base model.
The running .NET stack expects that artifact to be downloaded before startup.

### Scaling Model

Only the `app` service is intended to scale out in normal usage:

- `nginx` remains the single public entry point.
- Multiple `app` instances handle incoming API traffic.
- A single `model` service performs inference for all app instances.
- All app instances share the same SQLite database volume.

## Setup

Running the demo.
You still need a fairly capable machine for local inference, and the base ONNX model will not be small just because the comments are smug.
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

Before starting the stack, download the base model artifacts:

		$env:HF_TOKEN = "<your token>"

That environment variable is not required for the public base model.

Then download the base ONNX package:

		dotnet run --project src/Companion.ModelPrep

That step is intentionally separate from application startup. It downloads the base Phi-3 package.
After that completes, the local model directory should contain:

- `models/base/Phi-3-mini-4k-instruct-onnx`

Start Docker. Then...

		docker compose up --build

### Mac OS X

1. Docker Desktop -> Settings -> Resources
2. Memory: start with 24 GB (if you have 32 GB RAM total)
3. CPUs: 6 to 8
4. Swap: 8 to 12 GB
5. Apply and restart Docker Desktop

Install the .NET 8 runtime if it is not already present. The prep tool targets `net8.0`, so having only a newer SDK installed is not enough for running it.

Prepare the base model artifacts first:

		dotnet run --project src/Companion.ModelPrep

This stack still uses CPU by default on Mac as well.

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

The tests mock out the ONNX model call. They replace the real runtime with a fake generator so the suite does not attempt to load the base model during normal test execution.

## Scaling

The application is split into two services: the API (`app`) and the model inference service (`model`). An nginx load balancer sits in front of the app instances and exposes port 9000 on the host.

To run multiple app instances against a single model service:

		docker compose up --build --scale app=3

All traffic to `http://localhost:9000` is automatically round-robin distributed across the app instances by nginx.

## Other things

### Model Preparation

The prep tool downloads `microsoft/Phi-3-mini-4k-instruct-onnx` to `models/base/Phi-3-mini-4k-instruct-onnx`.
The runtime uses its `cpu_and_mobile/cpu-int4-rtn-block-32` variant on CPU unless you later add a different execution provider on purpose.

The simplest local prep sequence on Windows is:

1. Install `Microsoft.DotNet.Runtime.8` with `winget`.
2. Optionally install `Microsoft.DotNet.SDK.8` if you also want local test and build support.
3. Run `dotnet run --project src/Companion.ModelPrep`.
4. Start the stack with `docker compose up --build`.

If you need to point at a different repository, pass arguments such as:

1. `--base-model-repo=microsoft/Phi-3-mini-4k-instruct-onnx`
2. `--force=true`

### Runtime Model Artifacts

This repository is a runtime and deployment project.

The supported workflow is:

1. Run `dotnet run --project src/Companion.ModelPrep` to download the base artifact.
2. Start the application with `docker compose up --build`.

The model service loads the base CPU ONNX package through `MODEL__ONNXPATH` and does not configure an adapter.

To verify the real ONNX Runtime base model outside the unit-test and coverage process, run:

```powershell
dotnet run --project tools/Companion.OnnxRuntimeSmokeTest
```

Pass `--generate` to create a generation request with an already-cancelled token. The smoke test is intentionally separate because native ONNX Runtime execution is not collected by unit-test coverage.

### Old dependency

The project keeps `Utf8Json` pinned as an intentionally old and dead dependency that still participates in token cache handling. That is not there because it is a good idea. It is there because the scenario is trying to preserve bad ideas on purpose.

## License

This work is a derivative of OWASP Cornucopia, used under the Creative Commons Attribution-ShareAlike 4.0 International (CC BY-SA 4.0) license.
This derivative work is also published under the same CC BY-SA 4.0 license.
While this license explicitly permits free commercial use, a significant amount of time and effort went into adapting and maintaining this resource.
If your organization derives commercial value from this material (e.g., for internal training, client audits, or commercial services), we kindly request that you consider supporting our ongoing work with a [voluntary donation](https://owasp.org/donate/?reponame=cornucopia&title=OWASP+Cornucopia).

## Attribution

The idea is based on [Engineers & Exploits](https://github.com/northdpole/engineers-and-exploits-the-quest-for-security) - A Cornucopia workshop.
