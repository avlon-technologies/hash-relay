---
name: implement-against-architecture
description: Gate implementation on an explicit model decision - classify a requested feature, change or refactor as needing an ADL change, a BDL change, both or neither; for anything but neither, inspect the authoritative model, propose the delta, prove what can be proved, and STOP for approval before writing code; then implement against the accepted model and check conformance. Use when asked to build, change, extend or refactor something in a repository that carries a continuum/ package, when a request might move the architecture or the business model, or when implementation has revealed that the model does not answer a question the code needs answered.
---

# Implement against the architecture (classify · gate · implement)

**Load the `continuum` skill first** — knowledge routing, tool routing,
diagnostics and evidence discipline live there. This skill adds one thing to
it: **a decision boundary that code must not cross unasked.**

It orchestrates and delegates. It authors no change format, runs no
compiler and defines no rule of its own:

```
this skill              WHETHER the model must move, and WHEN to stop
understand-architecture reading and explaining the model
change-architecture     authoring an .adl-change and proving apply(A, Δ) = B
development-package     turning an accepted architecture into an implementable handoff
reconcile-architecture  what to do when the guard says the two have drifted
the MCP tools           every mechanical answer
you                     the architecture judgment, and the implementation
```

## 0. The rule this exists for

> **An unrecorded architectural decision is still a decision. It is just one
> nobody approved, nobody can find, and the next reader has to reverse-engineer
> from code.**

Implementation that quietly assumes a new service, a new message, a new owner,
a new dependency or a new business rule has moved the architecture without
moving the model. That is the failure this skill prevents — not by forbidding
change, but by making it explicit and approved *before* it is encoded in code.

Two prohibitions, and they are absolute:

- **Never edit the authoritative model to unblock yourself.** Not
  `continuum/model/`, not a `.bdl` source, not "just to make it compile". The
  model moves by an approved change, and by nothing else.
- **Never treat "it compiles" or "it applies" as approval.** `continuum_apply`
  returns state B; it does not adopt it. A mechanically valid change can be a
  bad idea, and that judgment is the user's.

## 1. Classify the request — four outcomes

Do this **before** planning implementation, and state the outcome in one line
so the user can disagree with it cheaply.

| Outcome | What it means | Go to |
|---|---|---|
| **neither** | the model already answers every question the code needs | §2 |
| **ADL** | the structure changes: what exists, who owns it, what talks to what | §3 |
| **BDL** | what the business MEANS changes: a rule, a policy, a term, an outcome | §4 |
| **both** | the business meaning changes and its realization changes with it | §5 |

### How to decide, concretely

Read the model before answering — `continuum_query` for one element,
`continuum_text_projection` for a bounded slice, `continuum_process_graph` for
anything causal. Do **not** classify from the repository's code, its file
layout or its README: those are the implementation, and the question is whether
the *intent* covers what is being asked.

**It needs an ADL change if the request adds, removes, renames, moves or
re-owns any of:** an element (service, actor, resource, capability, message,
workflow, bounded context, domain), an edge (`dependsOn`, `publishes`,
`consumes`, `reads`, `writes`, `owns`, `realizes`, `deployedAs`), a constraint,
a requirement, or a policy. Also if it changes *who* is responsible for
something the model already declares.

**It needs a BDL change if the request changes what the business means** — a
capability's purpose, a policy or rule, a value definition, a term, an outcome,
a modality (is this *stated*, *required*, *observed*?), or the provenance of an
assertion. A business model answers "what does the organization do and mean",
independent of any system that realizes it.

**It is neither if** the change is entirely inside something the model already
declares: an algorithm, a data structure, error handling, performance, logging,
refactoring that preserves every declared element and edge, a test, a fix that
makes code match what the model *already* says.

**When the answer is genuinely unclear, say so and ask.** An honest "this could
be read two ways, and they lead to different work" costs one question. Guessing
costs a wrong model or a silent divergence.

## 2. Neither — implement

Say which declared elements the work sits inside, then build it. Nothing here
is ceremonial: naming them is what makes it checkable later that you stayed
inside them.

**If building it turns out to need something the model does not declare, that
classification was wrong — stop and go to §8.** "Neither" is a finding about the
model, not a commitment to finish without one.

At the end, confirm conformance rather than assuming it — §7.

## 3. ADL — the change artifact path

This is where `change-architecture` does the work. Delegate to it; do not
re-derive its steps here. What this skill adds is the **stop**.

1. **Establish state A.** `continuum_compile` with state semantics. Keep the
   three digest domains apart — attestation, change-state, content — and never
   substitute one for another.
2. **Decide the architecture in concepts first**, not in ADL syntax. What
   exists, what it owns, what it depends on, what it may not do.
3. **Author the `.adl-change`** into the repository's change record (in this
   workspace, `architecture/changes/`). A population or correction is a
   *change*, never a rewrite — that is what keeps element identity stable so a
   later `continuum_diff` reports a rename as a rename.
4. **Prove it mechanically**: `continuum_apply` to establish
   `apply(A, Δ) = B`, and compile B. Only `VERIFIED` and `INTENDED` claims may
   be declared; an `INTENDED` claim says so in its `desc`. `PROPOSED`,
   `UNKNOWN` and `CONFLICT` are not declared at all — they are gaps.
5. **Adding an element, edge, owner or reaction to silence a warning is not a
   change and is never permitted** — it is a lie with a digest behind it. An
   unrealized-intent warning can be the honest finding; explain it instead.

Then **§6 — present and stop.**

## 4. BDL — the source-diff path

**There is no `.bdl-change`, and you must not invent one.** BDL's own `ABSENT`
record is explicit:

> *"BDL change semantics do not exist yet — no diff, no apply, no change
> artifact, no change source. They are absent rather than stubbed, so that
> nothing downstream can depend on a shape that has not been designed."*

The MCP contract mirrors this in `BUSINESS_ABSENT`, which names each business
capability that deliberately does not exist and why — a BDL diff and a BDL
apply among them. **Read that record rather than guessing, do not emulate what
it says is absent, and do not describe a BDL change as if it were proved the way
an ADL change is.** The two paths are asymmetric because the languages are.

So the proposal is a **source diff plus a compile result**:

1. **Read the current business model.**
   `continuum_business_inspect` — `view` is a CLOSED set, `elements` or
   `worklist`, and there is no filter to pass because BDL has fixed
   projections rather than a query language. `continuum_business_text_projection`
   for the readable form.
2. **Write the proposed `.bdl` source as a candidate**, beside the
   authoritative document — never over it. `continuum_business_parse` answers
   whether it can be read as a BDL model at all.
3. **Compile the candidate.** `continuum_business_compile`. Its diagnostics
   carry `rule` and `jurisdiction` and travel with the result; nothing resolves
   a BDL rule id to a registry, so quote the rendered diagnostic rather than
   inventing a catalogue lookup.
4. **Diff the CANONICAL source, not your draft.** `continuum_business_emit`
   returns the model as authored `.bdl` source, so emitting both the current
   document and your compiled candidate gives two canonical forms to compare.
   Diffing your hand-written draft instead shows your formatting choices as if
   they were changes, and hides a real change that happens to reformat.
5. **Show the behavioural delta** — this is the part no tool will do for you,
   and it is why this step is prose rather than a digest:
   - the **source diff**, canonical form against canonical form;
   - the **projection comparison**: the `elements` view before and after, and
     the `worklist` view before and after;
   - the **validity comparison**: valid/invalid and every diagnostic that
     appeared or disappeared;
   - in words: which business rule, policy, term or outcome now means something
     different, and what follows for anyone relying on the old meaning.
6. **BDL never mints identity** (rule R3, identity stability). If a new element
   needs an id, the id is authored by a human, deliberately. Do not generate
   one, and do not reuse one.
7. If the repository carries both languages, `continuum-bdl`'s
   business-to-architecture correspondence checkers are the thing that says
   whether the business model and the ADL model still agree. Use them; do not
   assert correspondence by reading.

Then **§6 — present and stop.**

## 5. Both — coordinate, then present once

Order matters, and it follows from what the two languages are for: **BDL says
what the business means; ADL says how a system realizes it.** So:

1. Settle the **business** delta first (§4). Meaning is upstream of
   realization, and an architecture built to realize a rule nobody has agreed
   on is work done twice.
2. Derive the **architecture** delta from it (§3), and say explicitly which
   business change each architectural change serves.
3. Check **correspondence** between the two before presenting — a pair that
   does not correspond is not two approvals, it is one incoherent proposal.
4. Present **one** delta with both halves, and take **one** approval. Do not
   implement the ADL half while the BDL half is still open: the halves are not
   independently adoptable, and half a coordinated change is a divergence with
   a paper trail.

Then **§6 — present and stop.**

## 6. The stop

**Present, then stop. Do not continue into implementation in the same breath.**

What a reviewer needs, and nothing more:

- **what changes**, in architecture or business concepts — not in syntax;
- **why**, and what it makes possible that is not possible now;
- **the proof, labelled for what it is**: for ADL, `apply(A, Δ) = B` with the
  state digests; for BDL, the compile result and the projection comparison. Say
  which is which — a BDL proposal has no `apply` proof and must not appear to;
- **what it does not settle** — every question left open, as a gap rather than
  as an omission. A missing claim is a missing claim, not evidence of absence;
- **what you will implement** once it is accepted.

Then ask for approval, explicitly, and wait. Approval of the *model change* is
not approval of an implementation plan you have not shown, and approval of one
half of a coordinated change is not approval of the other.

## 7. After approval — implement, then check

For ADL in a client repository the change is a **transition**, and its
sequence is not yours to reconstruct: drive it with the readiness loop
(`continuum` skill §1b). In the order readiness will show it — the person
**approves** (one command, from readiness); you **materialize** the sanctioned
target; you **implement** against `result/`, make the complete resulting suite
pass and **commit**; the person **accepts** (one command); you **adopt**.
Implementation begins only once the approval fact exists — an approval asked
for after the code is written does not authorize that code (§7.2 of the
lifecycle). Adoption comes last, after acceptance, never before implementation.

1. **Prepare the approved target.** For ADL, read readiness and perform the
   agent steps it awaits — at this point, materialize; the `.adl-change` stays
   as the explanation of the transition. For BDL, edit the authoritative
   `.bdl` source, then recompile and re-inspect to confirm the source is the
   source that was approved.
2. **Implement against the accepted model**, not against your memory of the
   proposal. Re-read it if the work is long. **The moment the work needs an
   answer the accepted model does not carry, stop and go to §8** — including
   when the missing piece looks like a small extension of what was just
   approved. That is the case this skill exists for, and it is the one most
   easily walked past while there is momentum.
3. **Check conformance rather than assuming it.** `continuum_guard` for what
   the Architecture Guard would say on this evidence; `continuum_package_check`
   where a development package is built; `continuum_icp_check` for an
   incremental change package. If the guard reports drift, `reconcile-architecture`
   owns what to do — do not hand-repair state it is built to reason about.

   **Read the lifecycle before you reason about it, and never write it by
   hand.** The loop, who performs each act, and the one-command-at-a-time rule
   are the `continuum` skill §1b — follow it there rather than from here.
   `continuum_workflow_verify {packageDir}` says whether the workflow record
   itself verifies; when it does not, no state read from it is trustworthy.

   **"The check did not run" is not "the check passed", and you must not report
   it as one.** A guard invocation can come back without a judgment for reasons
   that have nothing to do with the architecture: no credential, an unreachable
   Host, a route the deployment does not implement, or a package that was never
   built (`package check` needs a `PACKAGE.json`, and a repository laid out
   exactly as documented but never packaged has none). A git hook may well let a
   commit through in that state, and locally that is deliberate. It is still not
   evidence of conformance.

   So read the guard's answer for WHICH outcome it is — PASS, FAIL, INDETERMINATE (exit 2,
   or 3 where a judgment was required) or NOT APPLICABLE; never report an absent answer — and say which one you got. If it is the third, the
   honest report is "conformance was not established, because X", and X belongs
   in the report next to whatever you built.
4. **Report what was approved, what was built, and what still differs.** A
   difference you can name is a finding; one you cannot is a divergence.

## 8. When implementation outruns the model

This is the case the whole skill exists for, and it will happen: you are
building, and you find the model does not answer something the code must
answer. A name it does not declare. An owner it does not name. A message with
no publisher. A rule the business model leaves open.

**Stop. Return to §1 and re-classify.** Then:

- **Do not** infer the answer and encode it in code.
- **Do not** edit the model to match what you were about to build.
- **Do not** widen the change already approved on the grounds that it is
  "nearly the same change". It was approved as what it was.
- **Do** say precisely what is missing, what the code needs from it, and what
  the candidate answers are — then take it back through the gate.

Partial work is fine and should be kept: implement everything that does *not*
depend on the missing decision, and say exactly what you left out and why.
Scaling the work down is the user's call, not yours.

If the gap is real but nobody is ready to decide it, **record it as a gap**
rather than leaving it in your head. An open question with a name is an asset;
the same question inside an implementation is a defect waiting to be found.

## 9. What this skill never does

- Writes code that depends on an unapproved model change.
- Edits `continuum/model/` or an authoritative `.bdl` source to unblock itself.
- Calls a change adopted because it compiled, applied, or passed a test.
- Hands a person a chain of lifecycle commands, or any command readiness does
  not name as the awaited human step.
- Implements a semantic change before an approval fact binds its transition.
- Invents a `.bdl-change`, a BDL diff, or a BDL apply.
- Mints a BDL identity.
- Adds an element, edge, owner or reaction to silence a compiler warning — not
  once, not to get a clean run, not ever.
- Infers technology. A `Resource [data-store]` is not a product name.
- Reports an architecture question answered from code, prose or a generated
  view when the model is what was asked about.
