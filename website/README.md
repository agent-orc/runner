# Runner website

The static product site is published at <https://agent-orchestrator.dev/runner/>.
Changes under `website/` on `main` are published to the `deploy` branch by
`deploy-website.yml`. The existing hosting timer then pulls that branch. A
successful Actions run alone does not prove the live site has updated.

## Ecosystem link

The header retains its five native disclosure menus. GitHub and the Agent
Orchestrator link share the adjacent utility group. At narrower widths their
labels become compact; mobile keeps the existing menu row below the product
identity. Product content, fonts, fragment targets and menu scripts are unchanged.

`family-navigation.css` implements `family-link-v1` and is kept byte-identical to
Token Economy's local copy. The orange dot is decorative. The accessible name
`Agent Orchestrator home` and normal same-tab destination remain present when
the visible label is `AO`. GitHub retains its existing separate-tab behavior.

Maintain the [Marketing Studio contract](https://github.com/RobertMischke/agent-studio-marketing/blob/main/02-produktname/dachmarke-und-produktseiten-header.md) with any shared
change. Verify fragment targets, shared CSS parity, keyboard disclosures and
header packing at mobile, intermediate and wide viewports. Confirm the live
HTML and stylesheet after the hosting timer deploys the published branch.
