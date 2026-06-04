---
title: CLI Setup for Developers
description: Point Claude Code, Codex, or any OpenAI-compatible tool at your FoundryGate gateway.
sidebar:
  order: 2
---

import { Steps, Aside, Tabs, TabItem } from '@astrojs/starlight/components';

You've been granted access by your FoundryGate admin. This guide shows you how to connect Claude Code, Codex CLI, or any OpenAI-compatible client to the gateway in under five minutes.

## What you need

- Your **APIM subscription key** — visible on the FoundryGate `/me` page
- Your **APIM gateway URL** — shown in the "Configure your AI CLI" panel on `/me`
- The **model deployment name** you want to use — listed in the same panel

<Aside>
If you don't see a key on `/me`, ask your admin to provision one for your account.
</Aside>

---

## Verify your key

Before configuring any tool, confirm the key works:

```sh
curl https://<your-gateway-url>/openai/deployments/<model-name>/chat/completions?api-version=2024-02-01 \
  -H "api-key: <your-key>" \
  -H "Content-Type: application/json" \
  -d '{"messages":[{"role":"user","content":"ping"}],"max_tokens":5}'
```

Expected: a JSON response with a `choices` array. A `401` means the key is wrong or the subscription is suspended (quota exhausted — contact your admin).

---

## Configure your tool

<Tabs>
  <TabItem label="Claude Code">

  Claude Code supports any OpenAI-compatible endpoint via the `apiBaseUrl` config key:

  ```sh
  claude config set apiBaseUrl https://<your-gateway-url>/openai
  claude config set apiKey <your-key>
  ```

  Then set your model:

  ```sh
  # Use the deployment name shown on your /me page
  claude config set model <deployment-name>
  ```

  Test it:
  ```sh
  claude "Say hello from FoundryGate"
  ```

  </TabItem>
  <TabItem label="Codex CLI">

  Codex CLI uses `OPENAI_BASE_URL` and `OPENAI_API_KEY`:

  ```sh
  export OPENAI_BASE_URL=https://<your-gateway-url>/openai
  export OPENAI_API_KEY=<your-key>
  export OPENAI_MODEL=<deployment-name>
  ```

  Add these to your shell profile (`~/.zshrc`, `~/.bashrc`) to persist across sessions.

  Test it:
  ```sh
  codex "Say hello from FoundryGate"
  ```

  </TabItem>
  <TabItem label="Python / OpenAI SDK">

  ```python
  from openai import AzureOpenAI

  client = AzureOpenAI(
      azure_endpoint="https://<your-gateway-url>",
      api_key="<your-key>",
      api_version="2024-02-01",
  )

  response = client.chat.completions.create(
      model="<deployment-name>",
      messages=[{"role": "user", "content": "Hello from FoundryGate"}],
  )
  print(response.choices[0].message.content)
  ```

  </TabItem>
  <TabItem label="curl / generic">

  Any OpenAI-compatible client works. The key headers are:

  | Header | Value |
  |---|---|
  | `api-key` | Your APIM subscription key |
  | `Content-Type` | `application/json` |

  Base URL pattern:
  ```
  https://<gateway-url>/openai/deployments/<deployment-name>/chat/completions?api-version=2024-02-01
  ```

  </TabItem>
</Tabs>

---

## Manage your quota

Your token budget resets on the 1st of each month. You can:

- **Check current usage** on the `/me` page (quota gauge shows tokens used / allocated).
- **Request a quota increase** via the "Request Quota Increase" button on `/me`.
- **Rotate your key** from `/me` if you suspect it's been compromised.

<Aside type="tip">
Token counts reset at 00:01 UTC. If you're near your limit at month end, a small delay may restore access automatically.
</Aside>

## Quota exhausted?

If your key returns `401` mid-month, your allocation is exhausted and your APIM subscription has been suspended. Submit a quota increase request on `/me` — your admin will receive a notification.
