# moz - Development Guidelines

## 1. Project Context

moz is a CLI tool that simulates PLC communication.

It starts multiple Modbus TCP/RTU servers from a configuration file and provides simulated device memory that can be accessed through multiple protocols.

The project is designed around the following principles:

- Simple architecture with a small number of packages.
- In-process protocol implementations.
- Explicit interfaces at integration boundaries.
- Specification-driven development.
- Structured communication logging.
- Maintainability and debuggability over extensibility for its own sake.

The project originated from lessons learned during the development of karakuri.

## 2. Documentation and Sources of Truth

Read the following documents before making architectural or behavioral changes.

| Document             | Purpose                                                       |
| -------------------- | ------------------------------------------------------------- |
| `docs/consept.md`    | Project vision, architecture, design decisions and anti-scope |
| `docs/spec-*.md`     | Detailed implementation specifications                        |
| Existing source code | Actual implementation and current behavior                    |
| Tests                | Executable behavioral expectations                            |

Follow these rules:

- Treat `docs/consept.md` as the architectural intent.
- Treat `docs/spec-*.md` as the behavioral specifications.
- Treat tests as executable verification of the specifications.
- Do not assume documentation and implementation are always synchronized.
- When they disagree, identify the discrepancy before making changes.
- Do not silently change specifications to accommodate an implementation.
- Update relevant specifications when behavior or design intentionally changes.

Before implementing a feature, identify the relevant specifications and tests.

## 3. Architectural Principles

### 3.1 Keep the Architecture Simple

Avoid unnecessary architectural complexity.

- Do not introduce deep layered architecture.
- Do not create abstractions without a concrete use case.
- Keep the number of packages and files small.
- Prefer code that can be understood by reading a small number of files together.
- Follow existing package boundaries and dependency directions.

Do not introduce patterns merely because they are considered best practices in larger systems.

### 3.2 No Process-Separated Plugins

All protocol implementations must run in the same process.

- Do not introduce gRPC-based protocol plugins.
- Do not introduce dynamic assembly loading.
- Do not introduce AssemblyLoadContext-based plugin systems.
- Do not create plugin manifests or plugin discovery mechanisms.

Protocols are separated through namespaces, interfaces and ordinary in-process composition.

### 3.3 Explicit Composition

Configuration parsing and implementation selection belong to the CLI layer.

- Keep the factory responsible for selecting and constructing implementations in one explicit location.
- Prefer explicit switch-based composition.
- Do not introduce DI containers or reflection-based discovery.
- Do not let core protocol implementations depend on CLI or configuration types.

### 3.4 Dependency Direction

Dependencies must follow the established architecture.

- CLI depends on Core.
- Modbus implementations depend on Devices and Logging abstractions.
- Devices must not depend on protocol implementations.
- Core components must not depend on CLI-specific concerns.

Do not introduce circular dependencies.

## 4. Core Design Boundaries

### 4.1 Devices

`IDeviceAccess` is the common interface for accessing simulated device memory.

It is the shared integration boundary for protocol implementations and future consumers.

The memory model consists of four areas:

- Bits (read-write)
- ReadOnlyBits
- Words (read-write)
- ReadOnlyWords

The names are protocol-neutral on purpose: Modbus maps coils / discrete inputs / holding registers / input registers onto them, MC and FINS map their devices and areas onto them. Do not reintroduce protocol vocabulary into the Devices layer.

The `Watch` mechanism provides notifications of memory changes.

Rules:

- Keep protocol-specific behavior out of the Devices layer.
- Keep the memory model independent of transport protocols.
- Do not duplicate device memory for individual protocols.
- Use existing interfaces and notification mechanisms.
- Do not introduce a second memory abstraction without a concrete requirement.

Typed named variables are an overlay on device memory. Do not mix variable-specific behavior into the fundamental memory access interface.

### 4.2 Modbus

Keep the Modbus implementation divided into the established responsibilities.

**PDU / Codec**

- Handle Modbus PDU parsing, construction and encoding.
- Keep protocol behavior independently testable without sockets.
- Share protocol logic between TCP and RTU.
- Validate function codes, lengths, addresses and quantities according to specifications.

**Transport**

- TCP handles MBAP framing and TCP connections.
- RTU handles serial framing, CRC16 and frame timing.
- Transport-specific behavior must not leak into shared PDU processing.

**Memory Access**

- Use `IDeviceAccess` for device memory operations.
- Do not access internal device storage directly.

Keep parsing, encoding and memory mutation responsibilities explicit. Do not describe stateful memory operations as pure functions.

### 4.3 Logging

Use `Microsoft.Extensions.Logging` as the unified logging abstraction.

- Use `CommLog` for centralized communication-related EventId definitions.
- Preserve consistent event semantics across text and JSON formatters.
- Record communication failures systematically.
- Include sufficient context to diagnose malformed frames, connection failures and listener errors.
- Do not introduce an independent communication logging framework.
- Avoid unnecessary logging abstractions.

Future logging consumers should be integrated through `ILoggerProvider`.

## 5. Implementation Guidelines

### General

- Prefer simple and explicit implementations.
- Avoid premature abstraction.
- Avoid speculative extensibility.
- Preserve existing behavior unless the specification explicitly requires a change.
- Minimize unrelated modifications.
- Avoid unnecessary dependencies.
- Follow existing naming and formatting conventions.
- Handle errors explicitly.
- Do not silently swallow exceptions or failures.

### C#

- Follow the existing .NET version and project conventions.
- Use asynchronous APIs appropriately.
- Consider cancellation and resource disposal.
- Avoid unnecessary allocations in communication-critical paths.
- Do not introduce unnecessary interfaces or generic frameworks.
- Keep concurrency behavior explicit and testable.

### Protocol Implementation

- Validate untrusted communication data.
- Handle malformed frames without corrupting device state.
- Respect protocol-specific limits and framing rules.
- Keep TCP and RTU transport behavior separate.
- Ensure shared protocol logic remains reusable.
- Consider partial reads, disconnects and cancellation.

### Configuration

- Follow the existing configuration schema.
- Keep validation behavior consistent with `moz validate`.
- Do not introduce configuration hot reload.
- Do not silently accept invalid or ambiguous configurations.
- Update the relevant configuration specification when the schema changes.

## 6. Testing and Verification

Specifications and tests must evolve together.

For new features and bug fixes:

1. Identify the relevant specification.
2. Understand the existing implementation and test coverage.
3. Identify normal, boundary and error cases.
4. Add or update tests.
5. Implement the minimum necessary changes.
6. Run relevant tests.
7. Review the changes for regressions.

Testing guidelines:

- Prefer deterministic tests.
- Test protocol processing independently from network transport.
- Use memory-based streams or pipes for transport integration tests where appropriate.
- Test malformed frames and connection failures.
- Test boundary values and invalid configuration.
- Do not rely exclusively on successful communication scenarios.
- Do not weaken tests simply to make implementation pass.
- Do not claim tests passed unless they were actually executed.

When a test cannot be executed, report the reason explicitly.

## 7. Development Workflow

For non-trivial tasks, follow this workflow.

### Step 1: Investigate

- Read relevant specifications.
- Inspect existing implementations.
- Identify affected packages and dependencies.
- Check existing tests.
- Identify potential conflicts with architectural principles.

### Step 2: Plan

Before implementation, explain:

- What needs to change.
- Why the change is necessary.
- Which files and components are affected.
- Which tests need to be added or modified.
- Whether documentation needs updating.

For small and straightforward changes, avoid unnecessary planning overhead.

### Step 3: Implement

- Make the smallest change that satisfies the requirement.
- Preserve established package boundaries.
- Avoid unrelated refactoring.
- Do not introduce speculative functionality.

### Step 4: Verify

- Run relevant tests.
- Run the build.
- Run available static analysis and formatting checks.
- Review the final diff.
- Check for unintended behavior changes.

### Step 5: Report

Provide a concise report containing:

- Changes made.
- Design decisions.
- Tests and commands executed.
- Test results.
- Remaining issues or limitations.
- Documentation updated.

## 8. Documentation Guidelines

Documentation is part of the implementation.

- Keep `docs/consept.md` synchronized with architectural decisions.
- Keep `docs/spec-*.md` synchronized with behavioral changes.
- Prefer concrete descriptions over abstract explanations.
- Clearly distinguish implemented behavior from planned behavior.
- Document constraints, error handling and edge cases.
- Do not claim that a feature is supported unless implementation and tests verify it.

When changing a specification, explain why the change is necessary.

## 9. Explicit Anti-Scope

Do not implement the following unless explicitly requested:

- Process-separated plugins.
- gRPC plugin infrastructure.
- Dynamic assembly loading.
- Plugin manifests and discovery.
- DI containers.
- Reflection-based factory discovery.
- Configuration hot reload.
- GUI.
- REST API.
- Register editing UI.
- TLS and authentication.
- Database integration.
- Trimming and AOT support.
- OpenTelemetry and metrics infrastructure.

Do not introduce these features as incidental improvements or future-proofing.

## 10. Decision Making

When multiple implementation approaches are possible:

1. Prefer the approach consistent with the existing architecture.
2. Prefer fewer abstractions and dependencies.
3. Prefer explicit behavior over implicit magic.
4. Consider debugging and maintenance costs.
5. Explain meaningful trade-offs.

Do not make irreversible architectural decisions based on assumptions.

When requirements are ambiguous or specifications are incomplete, ask for clarification rather than inventing behavior.

The goal is to keep moz understandable, testable and maintainable as its functionality grows.
