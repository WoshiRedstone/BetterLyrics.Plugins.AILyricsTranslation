# AGENT.md

This project keeps its agent-facing documentation in **[`AGENTS.md`](AGENTS.md)** — the
plural file name is the convention most coding agents (and this repository's own tooling)
look for, so the full guide lives there.

If you were told to read `AGENT.md`, read [`AGENTS.md`](AGENTS.md) instead. It covers:

1. what the plugin is and which behaviours of the original Python tool are preserved;
2. the file map and the one-way dependency direction;
3. the full data flow of a single translation;
4. how to build the `.blp` (including the **build twice** requirement) and the five pitfalls;
5. how to verify a change with the four harnesses;
6. how to verify inside the real host GUI;
7. a change checklist;
8. the licence situation (MIT source, GPL-3.0 binaries).
