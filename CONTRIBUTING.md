# Contributing to Fusebox

Thanks for helping. This page is what you need to build it, change it, and have a change accepted.

## Set up

You need the .NET SDK named in [global.json](global.json). Docker is needed too, but only for the tests that use a real PostgreSQL or Redis: without it they are skipped, not failed.

```powershell
dotnet build Fusebox.slnx
dotnet test Fusebox.slnx
dotnet format Fusebox.slnx --verify-no-changes
```

To see the whole system working, run `docker compose up --build` and open Grafana at http://localhost:3000. The [README](README.md) says what to expect.

## How the code is laid out

Dependencies point inwards:

```
Gateway (the host, YARP and middleware)  →  Infrastructure (Redis, PostgreSQL, HTTP clients)  →  Application  →  Domain
```

- **Domain** holds the plain types: money, tenants, budgets, prices. No I/O.
- **Application** holds the rules and the ports (interfaces) they need. The decisions live here as pure code that tests can run without a database: the budget guard, the anomaly detector, the evidence engine, the alert policy.
- **Infrastructure** implements the ports against real systems.
- **Gateway** wires it together and serves requests.

New behaviour usually means a rule in Application, a port if it needs the outside world, an adapter in Infrastructure, and a line of wiring in the host.

## What a good change looks like

- **Tests come with it.** Rules are tested without I/O. Anything that touches PostgreSQL or Redis is tested against the real one (the existing tests start a container). A bug fix starts with a test that fails.
- **No warnings.** Every warning fails the build, and `dotnet format` must be clean.
- **Logging is structured.** Use `[LoggerMessage]` with named parameters, never string interpolation, and never log a key, a prompt, or a response.
- **Money is whole micro-dollars.** Never `double` or `decimal` inside Fusebox. Dollars appear only at the edges, such as the admin API.
- **Time comes from `TimeProvider`**, so tests can move it.
- **Package versions live in one place**, [Directory.Packages.props](Directory.Packages.props).
- **Fail in a defined way.** Say what happens when a dependency is down, slow, or wrong, and test it. Background work must survive a bad round and try again.
- **Keep prompts and responses out of storage.** Fusebox records metadata only.

## Decisions are written down

A change that makes or reverses a design choice needs an [architecture decision record](docs/adr/README.md): the options, what was chosen and why, and when to revisit it. If a change alters how an existing decision works, update that record's notes in the same change, including what is **not** done and what was **not** verified. A record that only lists strengths is not finished.

## Documentation

Write for someone who has not seen the code: short sentences, plain words, numbers with their units. Do not describe work in terms of the order it was done in. Say what the system does now and what it does not do yet.

## Proposing a change

1. Open an issue for anything bigger than a small fix, so the approach can be agreed first.
2. Keep each pull request to one thing.
3. In the description, say what changed, why, and how you checked it. If a test is skipped or something was not run, say so.

## Reporting a security problem

Please do not open a public issue. Use the repository's private security advisory feature to report it, and include what you found and how to reproduce it.

## License

By contributing you agree that your contribution is released under the [MIT License](LICENSE).
