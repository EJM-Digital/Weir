# Execution Plans

Execution plans live in [GitHub issues](https://github.com/jampat000/Weir/issues). Open an issue (or an
epic with sub-issues) for work that is too large or risky for a single change: anything that touches
several modules, changes release or installer behavior, changes file deletion or mutation safety,
changes data migrations, or needs staged validation across Windows, Docker, server and web.

A plan issue should state the goal, the current state, the scope and non-goals, acceptance criteria
(including the tests or smoke checks that must pass), the steps, and decisions as they are made.

Small bug fixes can stay in the PR description if they have clear acceptance criteria and validation.

A plan that is a document in its own right, such as one that moves a whole suite, lives in [`active/`](active/) until it is done and is then moved to the archive.

Completed historical plans are kept in [`../archive/`](../archive/).
