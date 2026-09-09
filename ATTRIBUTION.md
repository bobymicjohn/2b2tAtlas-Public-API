# Attribution guide

Atlas attribution is optional and no permission is required, including for Atlas-authored
code in the full-stack example. A credit is a nice way to help people find the project.
The formats below
link to Atlas; individual records also include canonical and original-source URLs.

## Recommended wording

```markdown
Data provided by [2b2tAtlas](https://2b2tatlas.com).
```

Short form:

```text
Data: 2b2tAtlas.com
```

## README badge

```markdown
[![Data provided by 2b2tAtlas](https://img.shields.io/badge/data-2b2tAtlas-b45309)](https://2b2tatlas.com)
```

## Website footer

```html
<p>
  Location and historical map data provided by
  <a href="https://2b2tatlas.com" rel="source">2b2tAtlas</a>.
</p>
```

## Mod metadata or About screen

```text
Historical location, group, highway, and WDL metadata: 2b2tAtlas.com
```

If a record includes `canonicalUrl` or `interactiveUrl`, link the user to that entity rather than only the Atlas home page.

## Discord bots

Put a compact source line in an embed footer and link the location/group name to its `interactiveUrl`:

```text
Historical data: 2b2tAtlas
```

## Machine-readable metadata

Web projects can identify the source in JSON-LD:

```json
{
  "@context": "https://schema.org",
  "@type": "Dataset",
  "name": "My 2b2t tool dataset",
  "isBasedOn": "https://2b2tatlas.com/dataset.json",
  "provider": {
    "@type": "Organization",
    "name": "2b2tAtlas",
    "url": "https://2b2tatlas.com"
  }
}
```

## Keeping record-level provenance

An Atlas credit is not a replacement for original-source information attached to evidence or media. When convenient:

- retain `sourceUrl`, `attribution`, and `caption` for attachments;
- retain highway/group evidence links when showing an attribution claim;
- use `canonicalUrl` as the public citation for an Atlas entity;
- do not imply that Atlas created third-party screenshots, videos, maps, or wiki text.

These are provenance suggestions, not Atlas licensing conditions. Original creators' or sources' separate terms may still apply to third-party assets.
