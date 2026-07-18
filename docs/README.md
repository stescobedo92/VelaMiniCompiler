# Vela documentation (Mintlify)

## Preview locally

```powershell
cd docs
npx mint@latest dev --no-open
```

Open [http://localhost:3000](http://localhost:3000).

## Structure

- `docs.json` — site config, theme (Linden), version navigation for 0.3.0
- `getting-started/`, `guides/`, `language/`, `reference/`, `release/` — MDX pages
- Legacy DocFX articles remain under `articles/` for archive; the Mintlify site is primary

## Production

- CI validates links with `mint broken-links`
- GitHub Pages hosts a lightweight portal that points readers here
- Optional: connect the Mintlify GitHub app to this repo with docs path `/docs` for a hosted Mintlify deployment
