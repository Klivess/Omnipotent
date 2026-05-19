# Omnipotent

> A self-hosted automation platform that runs 25+ concurrent services — managing everything from financial bots to hardware devices to an intelligence network.

**[klive.uk](https://klive.uk)** &nbsp;|&nbsp; C# / .NET &nbsp;|&nbsp; 586+ commits &nbsp;|&nbsp; 24/25 services online

---

Omnipotent is the backend engine behind **Klives Management (KM)** — a personal command-and-control platform built from the ground up to automate, monitor, and operate every corner of my digital and physical world. It runs continuously, exposing a full REST API consumed by a React frontend and a growing ecosystem of hardware and software clients.

The project spans financial automation, passive intelligence gathering, social media automation, IoT device management, cloud file storage, AI-assisted task execution, cybersecurity telemetry, and mechatronics design — all under one roof.

---

## Dashboard

<img width="1919" height="1079" alt="image" src="https://github.com/user-attachments/assets/89f66e02-b109-4b29-91f0-2652c61c916b" />

---

## What's Inside

### Intel

**Omniscience** — Passive intelligence and behavioural analytics engine. Ingests messages and attachments from linked accounts across platforms, profiles identities using LLMs, and generates dossiers on tracked subjects.

- 10,400+ indexed identities
- 3.3 million messages ingested across 865 conversations
- 219,000+ attachments catalogued

**Schemes** — Automated money-making operations.

| Scheme | Status | Description |
|---|---|---|
| CS2 Arbitrage Bot | Active | Scans 32,000+ CS2 marketplace listings to find and exploit price discrepancies between Steam and CSFloat. Best find: +174.5% profit margin. |
| Meme Scraper | Active | Periodically harvests media from 15 Instagram sources, building a 12,700+ asset library for automated content pipelines. |
| OmniGram | Active | Manages Instagram accounts with scheduled posting and engagement analytics powered by the Meme Scraper pipeline. |
| OmniTumblr | Active | Manages Tumblr blogs with OAuth-authenticated scheduled posting and folder-based content automation. |
| OmniTrader | Simulator | Backtesting and strategy deployment engine for live exchange trading. Simulation mode with full analytics. Live deployment planned. |
| OmniTube Bot | Planned | Full YouTube content automation — production, upload scheduling, and channel strategy optimisation. |

<img width="1919" height="1079" alt="image" src="https://github.com/user-attachments/assets/8c1bbbbc-2be5-4c4e-8c01-4c90c7efc411" />

**OmniDefence** — Real-time API security telemetry with threat profiling, IP reputation scoring, honeypots, and response tooling.
<img width="1919" height="1079" alt="image" src="https://github.com/user-attachments/assets/d2f8c6ac-0f42-49ad-869a-8b8f1c8ae8ba" />

---

### Klive Suite

**KliveCloud** — Self-hosted file storage and management with role-based access control (6 permission tiers), shareable links, and 1.86 TB of managed storage.

**KliveTech** — IoT device manager. Hardware gadgets built on the [KliveTech-Ecosystem](https://github.com/Klivess/KliveTech-Ecosystem) Arduino/C++ library connect directly to Omnipotent for remote control and monitoring.

**KliveAgent** — An LLM-backed AI secretary that acts as an executive layer over Omnipotent. Rather than just answering questions, KliveAgent receives instructions, plans multi-step tasks, and executes them directly within the platform — running scripts, coordinating services, and driving workflows autonomously. It maintains persistent memories, a task queue, and a full conversation history. Has processed 7.5 million tokens across 238 sessions with a 57.8% autonomous script success rate.

**KliveChat** — Internal messaging system.

**KliveTools** — General-purpose utilities.

**Stratum** — Agentic mechatronics design studio. Each hardware project lives here with revision history, attachments, and AI-assisted design workflows.
<img width="1919" height="1079" alt="image" src="https://github.com/user-attachments/assets/a169ed29-6ce9-4954-832d-db2f1857507d" />


---

### Ops

**Schedule** — Unified cron-style task scheduler across all services. Bot and scheme tasks are queued, timed, and tracked through a shared scheduling layer.

**Admin** — Platform-wide administration and configuration.

![Bot Schedule Page](https://github.com/user-attachments/assets/6fb20518-596b-43ee-8c00-db084a79a259)

![Bot Logs Page](https://github.com/user-attachments/assets/9a3f320e-b5cc-4a46-a208-8936bc5645b2)

---

## Architecture

Omnipotent is a monolithic C# / .NET backend exposing a REST API.

The frontend is a separate React application served at [klive.uk](https://klive.uk), which connects to the Omnipotent API.

Hardware clients use the [KliveTech-Ecosystem](https://github.com/Klivess/KliveTech-Ecosystem) C++ Arduino library to register themselves with the platform and receive commands.

A custom CA (`KMCA`) signs internal certificates for secure API communication.

---

## Related Projects

- [KliveTech-Ecosystem](https://github.com/Klivess/KliveTech-Ecosystem) — Arduino/C++ library for connecting hardware to Omnipotent
- [HevySharp](https://github.com/Klivess/HevySharp) — .NET NuGet wrapper for the Hevy API

---

Built by [Nourdin (Klivess)](https://github.com/Klivess) — CS & AI student at University of Bath.
