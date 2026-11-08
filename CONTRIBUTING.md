# Contributing

Small, focused pull requests are welcome.

- Build and test: `dotnet test` (needs the .NET 10 SDK and `git`). Warnings are errors.
- Every behaviour change needs a unit test; SSH is behind `ISshClient`, so tests never need real hosts.
- Safe defaults (key-only SSH, allowlists, secret refusal, no permission-skipping flags) must stay enforced by tests.
  Changes that loosen them need a clear justification in the PR.
- Never put real hostnames, IPs, key paths or credentials in code, tests, docs or issues. Use placeholders such as
  `build-01` and `user@build-01.lan`.
- Format with `dotnet format` before pushing; follow `.editorconfig`.
