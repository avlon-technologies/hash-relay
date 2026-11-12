# Development package — hash-relay-m2-desktop

> A Continuum **development package**: a mechanically verifiable implementation handoff consisting of a Goal, the semantic architecture, the technology bindings for one environment, and executable acceptance scenarios. Generated and verified by `tools/development-package.mjs`; `PACKAGE.json` is its manifest and proof.

```
GOAL.md                         why — intent, in prose (never adds a semantic claim)
model/hash-relay.adl            what — the semantic architecture (authored adl-source; AUTHORITY)
model/hash-relay.adl-manifest.json
model/hash-relay.adl.txt        the dense adl-text projection (derived, for reading)
model/*.adl-binding.json        how — technology per environment (applies to this exact attestation)
model/*.vocabulary-claims.json
gaps/                           the implementation's recorded semantic gaps (yours; client workspace, never package inventory)
model/compile-proof.json        the compiler's result, relayed verbatim (derived)
acceptance/scenarios/*.scenario.json   executable Given/When/Then, each traced to the model
acceptance/run.mjs, fake-model-provider.mjs   the shipped runner and deterministic fake provider
acceptance/adapter.mjs          the step bindings (the implementation writes this; a reference one may ship)
acceptance/traces/              derived process-graph traces per scenario
IMPLEMENTATION.md               the contract for the implementation agent (derived)
BEHAVIOUR.md                    the behavioural projection: declared order, unordered (potentially concurrent) steps, underspecified causality (derived)
PACKAGE.json                    manifest + proof: identities, digests, file inventory (derived)
```

```
The Goal tells us why.
The semantic ADL tells us what the system means.
The bindings tell us how this environment realizes it.
The acceptance scenarios prove the important behaviours.
The implementation realizes all of the above — and is not in this package.
```

| | |
|---|---|
| semantic package | `Avlon.HashRelay` `draft-1` (`adl/1.4`) |
| attestation | `sha256:60306b31735e0c39d70e8f8e04de6c8e92e388ab0a6ef9742e1447aa86f8b93a` |
| changeState | `sha256:61fd8f85cb390ca71bbcb90823fbfe94c422d92dfdad26ab22959c7b714ed493` |
| environment | `MacOSDesktop` (also bound: `LinuxDesktop`, `LocalDevelopment`) |
| scenarios | 19, resolved against the model; no adapter ships — the implementation writes one and runs them with `package run` |
| compiler warnings | 15 |

Start with `GOAL.md`, then `IMPLEMENTATION.md`. Verify the package before trusting it:

```
node <continuum>/tools/development-package.mjs check <this directory>
```
