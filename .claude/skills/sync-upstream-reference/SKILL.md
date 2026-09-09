---
name: sync-upstream-reference
description: Pull new work from the forked-from upstream (xberg-io/xberg) into .reference/ by replaying its commits with git am --directory=.reference, then re-derive the C# port against it. Use when asked to sync, update, or catch up with upstream, to merge upstream changes, or to bring .reference/ to a newer upstream revision.
---

# Syncing `.reference/` with upstream

`.reference/` is a verbatim copy of [xberg-io/xberg](https://github.com/xberg-io/xberg), the
project this repository was forked from. `X-Ray.Content` under `dotnet/` is a native C# port of
its extraction engine, and the only reason this tree exists is so the port can be re-derived
against the Rust it came from.

**Why this is not a merge.** Before the reorganisation, upstream's files sat at the repository
root and syncing was `git merge upstream/main`. They now live under `.reference/`, so every
upstream commit touches a path this tree does not have — a commit against `crates/xberg/src/x.rs`
has to land on `.reference/crates/xberg/src/x.rs`. A merge cannot do that. Replaying the commits
with a path prefix can, and `git am --directory=` exists for exactly this.

Do not "fix" this by rewriting upstream history with `filter-repo --to-subdirectory-filter` and
merging that: rewriting changes every SHA including the shared ancestors, so the result has no
common ancestor with this repository and the merge degenerates into a whole-tree conflict.

## Before you start

Read `.reference/UPSTREAM.md`. It records the last replayed upstream commit, and that is where
this sync begins. If it is missing or stale, stop and establish it (see **Recovering the base**)
rather than guessing — replaying from the wrong base either duplicates work or silently skips it.

Confirm the tree is clean and that `.reference/` has no local edits beyond the deltas
`UPSTREAM.md` lists:

```sh
git status --porcelain
```

Work on a branch, never on the default branch.

## Replaying

**1. Add upstream as a remote and fetch it.**

```sh
git remote add upstream https://github.com/xberg-io/xberg.git 2>/dev/null || true
git fetch upstream main
```

**2. Decide the range.** `BASE` is the last replayed commit from `UPSTREAM.md`; `HEAD_REV` is
where you are syncing to (normally `upstream/main`). Record the count so you can sanity-check
the patch series:

```sh
BASE=<from UPSTREAM.md>
HEAD_REV=upstream/main
git log --oneline "$BASE..$HEAD_REV" | wc -l
```

**3. Generate the patch series.** `--binary` matters — upstream carries binary fixtures and
images, and without it those patches apply as corrupt. `-M -C` preserves renames and copies, so
a large upstream refactor stays reviewable:

```sh
mkdir -p /tmp/upstream-sync
git format-patch --binary -M -C -o /tmp/upstream-sync "$BASE..$HEAD_REV"
ls /tmp/upstream-sync | wc -l
```

**If that count is lower than the `git log` count, merge commits were dropped** —
`format-patch` omits them by default, and a linear series then does not reproduce upstream's
tree. This is expected when upstream merges PRs rather than rebasing. Do not ignore it: the
verification step below is what catches the resulting drift, and **step 6 is not optional**.

**4. Replay onto `.reference/`.** The `--directory` flag prefixes every path in every patch:

```sh
git am --directory=.reference --keep-non-patch --whitespace=nowarn /tmp/upstream-sync/*.patch
```

`--keep-non-patch` stops `git am` from mangling subject lines that start with `[…]`. Commit
messages, authors and dates are preserved, so `git log .reference/` stays a faithful record of
upstream's history.

This has been checked end to end against a scratch pair of repositories: a text edit, a binary
file change and a `git mv` in one upstream commit all land correctly under `.reference/`, the
fork's own `dotnet/` tree is untouched, and subject/author/date survive.

**5. When a patch fails.** `git am` stops and tells you which one. Options, in order of preference:

- **A path that does not exist here.** Upstream may have moved files in a commit whose rename
  was dropped with a merge. `git am --show-current-patch=diff` to see it, apply the intent by
  hand, `git add` the result, then `git am --continue`.
- **A conflict on one of the local deltas in `UPSTREAM.md`** (today only `.gitignore`). Resolve
  in upstream's favour for anything upstream owns, keep the fork's line if it is one this
  repository added, then `git am --continue`.
- **A patch that is already applied.** `git am --skip`.
- **A series that is going badly** (more than a handful of manual resolutions). `git am --abort`
  and fall back to the snapshot route below. A snapshot loses per-commit history but is honest;
  a half-applied series is neither.

Never resolve a conflict by editing files under `dotnet/` — those are the port, not upstream.

**6. Verify the end state — always.** This is the step that catches dropped merge commits, and
skipping it means shipping a `.reference/` that silently does not match any upstream revision:

```sh
git diff --stat "$HEAD_REV": HEAD:.reference
```

Note the bare `:` — it names the whole tree at that revision, and comparing it against
`HEAD:.reference` compares upstream's root against our subtree. Empty output means `.reference/`
is exactly upstream at `HEAD_REV`; anything else is drift, listed file by file.

Reconcile by lifting the affected paths straight out of upstream into place:

```sh
git archive "$HEAD_REV" <path> | tar -x -C .reference
```

`git archive` writes upstream's paths relative to `.reference/`, so nothing needs moving
afterwards. Re-run the diff until it is empty, then commit — and say plainly in the message that
the tree was reconciled this way, and which paths.

**7. Record the new base.** Update the sync table in `.reference/UPSTREAM.md` to the new commit,
in the same commit as the replay. Without this the next sync has no starting point.

## Fallback: snapshot instead of replay

When the series will not apply, replace the tree wholesale. History is lost, but the result is
verifiable and clearly labelled:

```sh
git am --abort
git rm -r --quiet .reference && mkdir .reference
git archive "$HEAD_REV" | tar -x -C .reference
git add -A .reference
```

Then re-apply the deltas from `UPSTREAM.md`, verify with the `git diff --stat` above (it must come
back empty), and say in the commit message that this was a snapshot rather than a replay, and why
the replay failed.

## Then re-derive the port — the sync is not the job

Bringing `.reference/` up to date changes nothing about the shipped package. The work is
re-deriving the C# port against the new Rust, and `CLAUDE.md` ("Re-syncing after an upstream
merge") is the authority on how. In outline:

1. Materialise the fixture corpus (`git submodule update --init --depth 1 test_documents`, then
   `python3 test_documents/scripts/fetch_corpus.py`).
2. Regenerate the goldens against the new Rust with `dotnet/tools/xberg-reference-gen`, passing
   `--overwrite`. The diff against those goldens **is** the list of upstream changes still to
   port.
3. Triage by cluster, not by fixture (`XRay.Content.TestRunner --cluster`).
4. Fix against the Rust source, never against the golden. A golden says *that* something
   differs; only the Rust says what the rule is.
5. Expect unit tests to fail where upstream changed behaviour. A red test pinning the old rule
   is the correct outcome — update it and say so in the commit.

Two things to leave alone while doing that:

- **The OCR deviation.** It is deliberate, documented in `CLAUDE.md`, and a re-sync will read it
  as drift. Do not "restore" it toward upstream.
- **Scope.** Transcription, embeddings, NER, chunking, keywords and the server modes are upstream
  features the port does not carry. New upstream work in those areas is not a gap.

## Recovering the base

If `UPSTREAM.md` has no usable commit, find one rather than guessing:

- `git log --oneline --all --grep="upstream" -i` — past syncs and merges in this repository.
- `grep -n "was merged at" dotnet/TODO.md` — the port's own running log records each sync.
- Last resort, identify it by content: pick a distinctive upstream file and find the upstream
  commit whose blob matches the one in `.reference/`.

Write what you found, and how you established it, into `UPSTREAM.md`.
