# Runner website

The static product site is published at <https://agent-orchestrator.dev/runner/>.
Changes under `website/` on `main` are published to the `deploy` branch by
`deploy-website.yml`. The existing hosting timer then pulls that branch. A
successful Actions run alone does not prove the live site has updated.

## Ecosystem link

The header retains its five native disclosure menus. A back arrow and
`agent-orc` sit beside GitHub. The label stays the same at every width. At 360
pixels and below, GitHub is available in the Docs menu so the full product
name and back link fit in the first row. Mobile keeps the existing menu row
below the product identity. Product content, fragment targets and menu scripts
are unchanged.

`family-navigation.css?v=2` implements `family-link-v2` and is kept byte-identical
to Token Economy's local copy. The link inherits the header font and has no
visible border or separate background. Its accessible name is
`Back to Agent Orchestrator (agent-orc)`. It leads to the Agent Orchestrator
home page in the same tab; GitHub retains its existing separate-tab behavior.

Maintain the [Marketing Studio contract](https://github.com/RobertMischke/agent-studio-marketing/blob/main/02-produktname/dachmarke-und-produktseiten-header.md) with any shared
change. Verify fragment targets, shared CSS parity, keyboard disclosures and
header packing at mobile, intermediate and wide viewports. Confirm the live
HTML and stylesheet after the hosting timer deploys the published branch.
