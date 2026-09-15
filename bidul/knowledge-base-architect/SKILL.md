---
name: knowledge-base-architect
description: Reorganize, create, and maintain a repository KnowledgeBase as a simple topic-based documentation graph. Use when the user wants to restructure a flat knowledge base, create README navigation hubs, connect related Markdown topics, remove duplication, document a codebase, or keep documentation synchronized with code changes.
---

# Knowledge Base Architect

Build and maintain a KnowledgeBase that is easy for both humans and AI agents to navigate.

The core principles are:

1. Simplicity over cleverness.
2. Organize by topic and responsibility, not by arbitrary document numbers.
3. Use README files as navigation hubs.
4. Keep one canonical source of truth for each fact.
5. Connect related topics with explicit links.
6. Prefer short focused documents over giant catch-all files.
7. Preserve useful existing knowledge while improving its structure.
8. Do not invent facts that cannot be verified from the repository.

## When this skill is used

Use this workflow when:
- restructuring an existing `/KnowledgeBase`;
- generating documentation from a repository;
- converting a flat list such as `01-...md`, `02-...md`, `03-...md` into topic folders;
- creating or repairing internal Markdown links;
- consolidating duplicated documentation;
- adding documentation for a new application, library, infrastructure component, API, workflow, failure mode, or operational process;
- updating documentation after code changes.

## Target structure

Prefer a shallow topic-oriented tree. Example:

```text
KnowledgeBase/
├── README.md
├── architecture/
│   ├── README.md
│   ├── system-overview.md
│   ├── data-flow.md
│   └── decisions.md
├── applications/
│   ├── README.md
│   ├── gateway.md
│   ├── rules-api.md
│   ├── tb-publisher.md
│   └── tb-consumer.md
├── infrastructure/
│   ├── README.md
│   ├── rabbitmq.md
│   ├── redis.md
│   ├── elasticsearch.md
│   └── openshift.md
├── development/
│   ├── README.md
│   ├── configuration.md
│   ├── testing.md
│   └── cicd.md
├── operations/
│   ├── README.md
│   ├── observability.md
│   ├── troubleshooting.md
│   └── known-risks.md
├── reference/
│   ├── README.md
│   ├── api-reference.md
│   └── glossary.md
└── CHANGELOG.md
```

Do not create these folders mechanically. Derive the actual topic groups from the repository and current documentation.

## Information architecture rules

### 1. Root README is the map

`KnowledgeBase/README.md` must answer:
- What is this system?
- What are its major areas?
- Where should a reader go next?
- What documentation is authoritative?
- What are the most important cross-cutting flows?

It must link to topic-level README files.

Do not turn the root README into a full system manual.

### 2. Every topic folder gets a README

A topic-level `README.md` is an index/navigation page, not a duplicate of all child content.

It should contain:
- a short purpose statement;
- a list of documents in the topic;
- a one-line explanation for each document;
- links to related topics.

### 3. One document, one responsibility

A document should have one clear question or responsibility.

Good:
- `rabbitmq.md`
- `configuration.md`
- `gateway-request-flow.md`
- `retry-policy.md`

Avoid:
- `everything-about-backend.md`
- `misc.md`
- giant files containing unrelated subjects.

Split a document when its sections can be understood and maintained independently.

### 4. No arbitrary numbering

Do not use ordering prefixes such as:

```text
01-what-is...
02-core-components...
03-mission...
```

unless sequence itself is semantically required, such as a migration or tutorial.

Topic names should explain meaning without relying on file order.

### 5. One source of truth

When the same fact appears in several documents:
- choose the document that naturally owns the fact;
- keep the full explanation there;
- replace copies elsewhere with a short sentence and a link.

Never maintain several independent copies of configuration values, queue names, API rules, architecture facts, or operational procedures.

### 6. Link related knowledge

Every non-trivial document should end with:

```md
## Related topics

- [System overview](../architecture/system-overview.md)
- [Configuration](../development/configuration.md)
```

Use relative Markdown links by default.

If the repository explicitly uses Obsidian-style `[[wikilinks]]`, preserve that convention consistently instead.

### 7. Progressive disclosure

The navigation path should be:

```text
Root README
  -> Topic README
    -> Focused document
      -> deeper reference only when needed
```

A reader should not need to open a giant document to discover where information lives.

### 8. Keep hierarchy shallow

Prefer 2-3 levels under `KnowledgeBase`.

Do not create deep trees such as:

```text
KnowledgeBase/backend/services/core/internal/components/...
```

unless the repository is truly large enough to justify it.

### 9. Use clear names

Prefer lowercase kebab-case for files and folders.

Examples:
- `system-overview.md`
- `failure-handling.md`
- `rules-api.md`

Names should describe the topic, not the document's creation order.

## Required document pattern

For normal topic documents, prefer:

```md
# Topic name

One or two sentences explaining what this topic is and why it matters.

## Purpose

What responsibility this component/process has.

## How it works

The minimum information needed to understand the behavior.

## Key flows

Important flows, interactions, or lifecycle.

## Configuration

Only if relevant. Link to canonical configuration docs if configuration is shared.

## Failure modes / gotchas

Only verified behavior and known risks.

## Source references

Point to important source directories/files when useful.

## Related topics

- [...]
```

Do not force empty sections into documents where they add no value.

## Open questions and risks

Do not mix unknowns into authoritative documentation as if they are facts.

Maintain unknowns and verified risks separately.

Recommended:

```text
operations/
  known-risks.md
reference/
  open-questions.md
```

For open questions:
- state the question;
- state current evidence;
- mark uncertainty clearly;
- when resolved, move the verified answer into the correct canonical topic and remove the stale question.

For risks:
- describe the verified sharp edge;
- impact;
- affected component;
- mitigation/workaround if known;
- link to the owning topic.

## Repository analysis workflow

Before changing documentation:

1. Read the current `KnowledgeBase`.
2. Inspect the repository structure.
3. Identify applications, libraries, infrastructure, external integrations, configuration, CI/CD, tests, and operational tooling.
4. Search for facts already documented.
5. Detect duplicate or conflicting information.
6. Build a topic map.
7. Only then reorganize files.

When multiple branches or versions are explicitly in scope, inspect those sources before claiming the documentation is complete.

## Refactoring an existing KnowledgeBase

When reorganizing an existing KB:

### Phase 1 - Inventory

Create a temporary inventory containing:
- current file;
- topics inside it;
- proposed destination;
- duplicate content;
- uncertain content;
- links that will need updating.

### Phase 2 - Proposed map

Before destructive restructuring, present the proposed tree.

The map must be simpler than the existing layout.

### Phase 3 - Migrate

- Move useful content into canonical topics.
- Preserve technical meaning.
- Rewrite only when clarity improves.
- Do not silently drop details.
- Replace duplicates with links.
- Update internal links after moves.

### Phase 4 - Validate

Check:
- every Markdown link resolves;
- every topic is reachable from a README;
- no orphan docs remain;
- no obvious duplicate sources of truth remain;
- filenames and headings are consistent;
- no old numbered navigation remains unless intentionally kept.

### Phase 5 - Summarize

Report:
- old structure;
- new structure;
- files moved/renamed;
- merged duplicates;
- unresolved questions;
- documentation gaps discovered.

## Updating the KnowledgeBase after code changes

Whenever a code change materially changes behavior, configuration, interfaces, deployment, architecture, or operations:

1. Determine which existing topic owns the change.
2. Update that canonical topic.
3. Update navigation only if a new topic was introduced.
4. Update links if files moved.
5. Add a concise entry to `KnowledgeBase/CHANGELOG.md` when the change is documentation-significant.
6. Do not create a new document when an existing canonical topic is the correct owner.

## Changelog

Keep a lightweight documentation changelog:

```md
# Knowledge Base Changelog

## YYYY-MM-DD

- Updated `infrastructure/rabbitmq.md` for the new queue behavior.
- Added `applications/rules-api.md`.
- Moved duplicated retry details into `operations/retry-policy.md`.
```

Do not mirror the Git history. Record only meaningful documentation changes.

## Quality rules

The KnowledgeBase should optimize for:
- discoverability;
- low duplication;
- simple navigation;
- small context windows for AI;
- maintainability;
- verifiable facts.

Avoid:
- huge monolithic Markdown files;
- generic `misc`, `notes`, or `info` buckets;
- unexplained acronyms;
- content copied across multiple files;
- deeply nested folders;
- documentation generated solely from filenames without reading code;
- invented architecture assumptions;
- stale links.

## AI-friendly writing

Write so another AI agent can retrieve the minimum useful context.

Each file should:
- start with the actual topic immediately;
- use descriptive headings;
- keep unrelated information out;
- name real components consistently;
- explain relationships explicitly;
- link outward rather than duplicating;
- distinguish verified facts from assumptions.

## Preferred execution behavior

If asked to "rebuild", "restructure", or "clean up" the KnowledgeBase:

1. Analyze first.
2. Show the proposed topic tree.
3. Preserve content.
4. Apply the restructuring.
5. Validate links and reachability.
6. Show a concise migration report.

If the user explicitly asks to apply changes immediately, proceed without waiting for another confirmation.

## Definition of done

The task is complete only when:

- the root README acts as a useful map;
- each major topic has a navigation README;
- topics are grouped by responsibility;
- arbitrary numbered flat files are removed unless semantically necessary;
- duplicated facts have a canonical owner;
- related documents are linked;
- Markdown links are valid;
- unresolved questions are clearly separated from verified facts;
- the KB is easier to navigate than before;
- no useful existing knowledge was silently lost.
