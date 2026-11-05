# HashRelay's architectural decisions

HashRelay's architectural decisions (ADRs) are kept in a **decision space held by the Continuum Host**. The Host
judges each act against `decision-authority.json` and signs it. This directory holds:

| File | What it is | Who writes it |
|---|---|---|
| `decision-authority.json` | the policy: who may propose, accept, revoke or supersede which subjects | the owner, at `decision init` |
| `decision-space.json` | the pin: which space this repository belongs to (the digest of its genesis line) | `decision init` |
| `decisions.jsonl` | an export of the record: every act, chained and Host-signed | the CLI, never by hand |
| `DECISIONS.md` | the rendering reviewers read | `continuum decision export`, never by hand |
| `assertions.json` | the decision states HashRelay relies on (for example `status ADR-001 accepted`) | a maintainer |
| `check.sh` | the checks the **Decisions** workflow runs; runs the same way locally | a maintainer |

## Who may do what

- The **owner** may do everything, including `govern`.
- **`agent:claude-code`**, delegated by the owner, may only `propose`, `amend`, `check` and record `evidence` on
  `::HashRelay`.
- Every `accept`, `revoke` and `supersede` on `::HashRelay` needs a **person who is not the act's author**. An agent
  never accepts its own proposal; the Host refuses it.

## Checking locally

```sh
bash architecture/decisions/check.sh                                    # keys fetched from $CONTINUUM_HOST
KEYS_FILE=host-keys.json bash architecture/decisions/check.sh           # or from a file
```

This needs `@avlon/continuum-cli` 0.26.0 or later on PATH. Version 0.25.1 has no `export` or `assert`; the check then
fails with `unknown verb export`, and never passes.

Exit codes:

| Exit | Result | Meaning |
|---|---|---|
| 0 | PASS | everything checked |
| 1 | FAIL | the record is damaged or forged; `DECISIONS.md` is missing, stale or hand-edited; or an assertion does not hold |
| 3 | INDETERMINATE | nothing failed, but something could not be established, for example no signature could be checked |

## Updating the rendering after an act

```sh
continuum decision export --dir architecture/decisions --offline --keys host-keys.json --out architecture/decisions/DECISIONS.md
```

To see the record as it stood after event N, add `--at N` (CLI 0.27.0 or later). The output is marked HISTORICAL.

## What a green check does not mean

A PASS means the record is intact, its rendering is current, and the decisions HashRelay relies on are in the stated
state. It does **not** mean the code implements those decisions. That is established by the tests and the
architecture guard (`continuum.yml`), not here.
