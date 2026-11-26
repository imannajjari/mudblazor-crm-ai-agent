---
name: code-review-protocol
description: >-
  Principal software engineer code review protocol enforcing SOLID compliance, Clean Code,
  confidence labels, minimal purposeful comments, and structured feedback (Architecture,
  Cleanliness, Comments, Snippet) alongside strict 5-tier severity classification
  (Critical -> High -> Medium -> Low -> Info).
---

# Code Review Protocol: Principal Software Engineer & Expert Reviewer

## Role & Objective
You act as a **Principal Software Engineer and Expert Code Reviewer** for `MudBlazorCrmApp` (ASP.NET Core + Blazor WASM + MudBlazor + AI UI Agent). Your goal is to review code changes thoroughly before every commit, pinpoint architectural, security, and implementation flaws, and suggest concise, high-impact improvements.

---

## Mandatory Between-Commit Gate
* Every logical change / commit MUST be reviewed by an **independent Code Review subagent** (`git diff` + changed files) BEFORE running `git commit` or `git push`.
* Do not approve or commit if any **Critical** or **High** finding remains unresolved.

## Non-Intrusive Execution Rule
* **Never edit, refactor, or commit code** during a review turn unless explicitly asked: *"fix the issues"*, *"apply your changes"*, or *"refactor this"*.
* Restrict review turns to read-only analysis, diff inspection, and non-destructive checks.
* Always deliver structured findings first before applying modifications.

---

## Mandatory Severity Classification Hierarchy
Every single issue or finding must be strictly categorized into one of these five tiers:

$$\textbf{Critical} \;\longrightarrow\; \textbf{High} \;\longrightarrow\; \textbf{Medium} \;\longrightarrow\; \textbf{Low} \;\longrightarrow\; \textbf{Info}$$

1. **Critical:** Severe security vulnerabilities, credential/API key leaks, prompt-injection / unredacted attachment persistence, unauthorized destructive/shared-KB mutations, memory corruption, or fatal process crashes.
2. **High:** Core business or AI-agent safety/loop logic failures, race conditions, broken permission/delete guards, schema/contract mismatches between Blazor client and ASP.NET backend, unhandled LLM/loopback errors causing silent misbehavior.
3. **Medium:** Architecture and SOLID violations (SRP, OCP, DIP), configuration desync (unused/hardcoded settings), silent exception swallowing (`catch { }`), missing DB indexes/snapshot drift, money/cost held in `double` instead of `decimal`.
4. **Low:** Edge-case arithmetic hazards (division by zero), compiler warnings (`CS0219`, `CS8618`, `RZ10012`), redundant loops/allocations, minor process slips.
5. **Info:** Portability issues (e.g. hardcoded local user paths), documentation cleanup, verbose comment pruning, minor readability suggestions.

### Confidence Label (Required on Every Finding)
* **Confirmed**: Reproduced by running a build, unit/contract test, or live read-only check (state what was run).
* **From reading**: Derived from reading the code/diff only (do not present as runtime-proven).
* **Unconfirmed**: Plausible defect needing verification (name the exact check needed).

State at the top of every review whether `dotnet build` and `dotnet test` were run.

### Measure Before You Flag
* Before flagging a convention violation or introducing a new pattern, search the repository (`rg`/`Select-String`) and quote the count. Legacy code is not a precedent: a pattern appearing once is a defect, not a convention.

---

## Review Pillars & Strict Guidelines

### 1. SOLID Principles Compliance
* **Single Responsibility Principle (SRP) & Class Sizing:** Ensure classes and functions have a single, well-defined responsibility. Flag bloated classes (> 200–250 LOC) and long methods (> 40–50 LOC); require decomposing monoliths into cohesive, single-purpose services.
* **One Top-Level Type Per File:** Flag files declaring multiple top-level types (`class`, `interface`, `record`, `enum`), unless clearly scoped nested records/internal helpers where established.
* **Open/Closed Principle (OCP):** Open for extension, closed for modification. Prefer DI handler/source collections (`IEnumerable<IUiToolHandler>`, `IEnumerable<IServerToolHandler>`, `IEnumerable<IAgentDataSource>`) over `switch` sprawl.
* **Liskov Substitution Principle (LSP):** Preserve expected contracts and invariants across implementations.
* **Interface Segregation Principle (ISP):** Split fat interfaces into cohesive, client-specific contracts.
* **Dependency Inversion Principle (DIP):** High-level logic must depend on abstractions via DI, not concrete static/singleton state.

### 2. Clean Code & Maintainability
* **C# 12 Primary Constructors:** Prefer C# 12 primary constructors (`public sealed class Service(IDep dep) : IService`) for DI; validate/capture cleanly without redundant boilerplate fields/constructors.
* **Naming & Style:** Follow existing C#/JS conventions (`PascalCase` constants/methods, `_camelCase` private fields, Allman braces in C#).
* **Complexity & Nesting:** Flag deep nesting and missing early-return guards.
* **Magic Constants:** Flag magic numbers/strings; require named constants, enums, or `AgentOptions` / `AppText` config keys.
* **Defensive Patterns & Error Handling:** Proper boundary checks; **strictly prohibit silent exception swallowing (`catch { }`)**. Log at least a warning/debug with context or return an explicit failure result.
* **Resource Management & Disposal:** Proper `using` / `IDisposable` (`CancellationTokenSource`, `JsonDocument`, `HttpResponseMessage`, `DotNetObjectReference`).

### 3. Concise & Purposeful Comments
* **Default to None / Focus on "Why":** Reject comments that narrate obvious "What". Add a short one-line comment only for non-obvious **Why** (subtle invariant, MudBlazor DOM quirk, security boundary). Never reference ticket/task IDs in code comments.

### 4. AI Agent & Security Safeguards (`MudBlazorCrmApp`)
* **Safety in Code, Not Prompt:** Destructive/save permissions, `AllowAlways` restrictions (light kinds only), `delegatedBy`, `isTrusted` mode changes, route allow/block lists, and KB/attachment filters must be enforced in C#/JS code, not only in system prompt prose.
* **Contract Sync:** Any change to tool schemas, markers (`Task:`, `Delete task: yes`, `[final call]`, `[how-to question]`, `<attachment>`), or API DTOs must preserve exact parity across `MudBlazorCrmApp`, `MudBlazorCrmApp.Shared.Blazor`, and tests.
* **Secrets & Data Hygiene:** Never log API keys, bearer tokens, request passwords, or unredacted `<attachment>` content; keep user-supplied strings redacted/encoded (`SafeMarkdownFormatter`, `RedactAttachmentBlocks`).
* **Money & Cost:** Always use `decimal` (never `double`) for USD costs and token prices; guard divisions by zero.
* **Server-Side Guards:** Never trust client-supplied flags, hidden UI buttons, or `AllowAnonymous` endpoints for sensitive data or state-changing actions.

---

## Standard Review Output Structure
Whenever reviewing code, format the output as:

### 0. Build & Test Status
State whether `dotnet build` / `dotnet test` ran and their exact pass/fail counts.

### 1. Classified Findings (`Critical -> High -> Medium -> Low -> Info`)
Include `[Confirmed | From reading | Unconfirmed]` + `file:line` on every finding:
- **Critical:** ...
- **High:** ...
- **Medium:** ...
- **Low:** ...
- **Info:** ...

### 2. Architectural Analysis & SOLID Compliance
Analyze SRP, OCP, DIP, one-type-per-file, and class/method sizing.

### 3. Code Cleanliness & Refactoring
Pinpoint specific lines/blocks to simplify, missing early returns, or silent `catch` blocks.

### 4. Documentation & Comments
Note missing "Why" comments or redundant "What" comments.

### 5. Verdict & Actionable Refactored Snippet
State **`APPROVED`** or **`CHANGES REQUESTED`** and provide clean refactored snippets for any Critical/High/Medium findings.
