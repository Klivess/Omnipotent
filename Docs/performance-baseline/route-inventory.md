# Observed API route inventory

Live telemetry collected 2026-09-27 03:00 UTC, before the snapshot changes were deployed.
All durations are server-side milliseconds. p95 is approximate from a log-scale histogram
and is unstable for routes with few samples. A blank 24h entry means no observations
in that window. Pseudo series such as `(preflight)` are telemetry categories.

| Route series | 24h calls | 24h p95 ms | 7d calls | 7d p50 ms | 7d p95 ms | 7d max ms | 7d cumulative ms |
|---|---:|---:|---:|---:|---:|---:|---:|
| GET /omnidefence/overview | 68 | 2,069,920 | 806 | 6,799 | 8,086 | 6,621,609 | 52,378,617 |
| POST /batch | 17 | 2,069,920 | 53 | 64,685 | 2,069,920 | 6,621,647 | 29,917,484 |
| GET /omnidefence/requests | 63 | 731,827 | 796 | 106 | 2,859 | 6,446,428 | 23,185,141 |
| GET /omnidefence/ip | 10 | 2,069,920 | 251 | 37.6 | 517,480 | 6,290,709 | 19,965,880 |
| GET /projects/overview | 80 | 425 | 1,916 | 126 | 3,400 | 38,732 | 1,735,471 |
| GET /api/omnitrader/firm/overview | 12 | 64,685 | 31 | 22,870 | 108,787 | 126,613 | 1,055,101 |
| GET /klivemail/stats | 8 | 37,117 | 25 | 11,435 | 45,739 | 58,357 | 430,960 |
| GET /omnidefence/ip-map | 60 | 505 | 784 | 425 | 715 | 13,128 | 371,301 |
| GET /cs2arbitragebot/getscanalytics | 13 | 26,685 | 25 | 3,400 | 27,197 | 28,356 | 262,384 |
| GET /api/omnitrader/backtests | 2 | 64,685 | 4 | 54,393 | 75,007 | 75,007 | 200,205 |
| GET /KliveAPI/telemetry/traces | 11 | 22,399 | 39 | 0.698 | 22,399 | 22,399 | 117,506 |
| GET /api/omnitrader/deployments | 2 | 1,429 | 4 | 1,429 | 64,685 | 66,653 | 115,882 |
| GET /projects/cost-simulator | — | — | 7 | 300 | 91,478 | 92,239 | 101,219 |
| GET /omnidefence/ips | 60 | 150 | 783 | 89.3 | 253 | 4,030 | 86,278 |
| GET /api/omnitrader/firm/risk | 3 | 1,700 | 4 | 1,011 | 71,942 | 71,942 | 74,727 |
| GET /tripwires/events | 10 | 2,807 | 14 | 1.40 | 63,404 | 63,404 | 66,226 |
| GET /tripwires/summary | 10 | 2,805 | 14 | 1.40 | 63,401 | 63,401 | 66,222 |
| GET /cs2arbitragebot/balanceHistory | 3 | 30,077 | 3 | 8,086 | 30,077 | 30,077 | 37,719 |
| POST /admin/terminal/execute | — | — | 11 | 1,011 | 6,331 | 6,331 | 19,717 |
| GET /projects/list | 9 | 1.09 | 27 | 0.698 | 9,615 | 10,033 | 19,625 |
| GET /omnidefence/ip/fingerprint | — | — | 175 | 0.987 | 11.2 | 7,442 | 18,136 |
| GET /admin/portforwarding/list | — | — | 14 | 1,011 | 1,868 | 1,868 | 16,801 |
| GET /omniscience/stats/overview | 8 | 0.050 | 25 | 0.031 | 5,717 | 6,005 | 12,678 |
| GET /KMProfiles/GetCurrentProfile | 8 | 0.415 | 45 | 0.207 | 212 | 10,719 | 11,820 |
| GET /klivemail/messages | — | — | 3 | 3.95 | 8,086 | 8,235 | 8,239 |
| GET /KliveCloud/GetPreview | 5 | 996 | 15 | 357 | 996 | 996 | 6,686 |
| POST /admin/terminal/session/execute | — | — | 1 | 6,659 | 6,659 | 6,659 | 6,659 |
| GET /klivemail/mailboxes | — | — | 3 | 126 | 6,393 | 6,393 | 6,551 |
| GET /KliveAPI/telemetry/overview | 34 | 126 | 177 | 1.66 | 75.1 | 794 | 6,090 |
| (preflight) | 896 | 0.698 | 12,553 | 0.147 | 0.587 | 131 | 6,072 |
| GET / | 64 | 212 | 164 | 0.349 | 253 | 990 | 6,044 |
| GET /omniscience/briefing/preview | — | — | 1 | 5,717 | 5,717 | 6,003 | 6,003 |
| GET /api/logs | 9 | 5.58 | 27 | 5.58 | 1,202 | 2,529 | 5,652 |
| GET /tripwires/list | 2 | 4,808 | 4 | 1.97 | 4,808 | 4,909 | 5,087 |
| POST /KliveAPI/telemetry/rum | 155 | 1.17 | 2,150 | 0.247 | 9.39 | 243 | 4,454 |
| POST /omnidefence/fp | 28 | 150 | 99 | 0.830 | 150 | 1,180 | 4,068 |
| GET /GeneralBotStatistics/GetFrontpageStats | 9 | 0.698 | 86 | 26.6 | 89.3 | 753 | 3,699 |
| GET /kliveagent/notifications | 10 | 143 | 40 | 1.17 | 150 | 2,492 | 3,384 |
| (unmatched) | 331 | 0.494 | 2,287 | 0.207 | 1.17 | 1,364 | 3,243 |
| GET /KliveAPI/Statistics | 9 | 1.85 | 84 | 31.6 | 89.3 | 138 | 3,075 |
| GET /omniscience/persons | — | — | 1 | 2,859 | 2,859 | 3,030 | 3,030 |
| GET /System/UptimeStatistics | 9 | 1.66 | 84 | 26.6 | 89.3 | 147 | 2,930 |
| GET /memescraper/memeScraperAnalytics | 11 | 170 | 31 | 44.7 | 425 | 429 | 2,765 |
| POST /admin/terminal/session/open | — | — | 5 | 0.987 | 2,404 | 2,581 | 2,608 |
| POST /tripwires/create | 1 | 2,404 | 1 | 2,404 | 2,404 | 2,559 | 2,559 |
| GET /KliveAPI/telemetry/routes | 16 | 106 | 48 | 53.1 | 106 | 109 | 2,240 |
| GET /projects/cache-health | 74 | 1.17 | 1,894 | 0.494 | 0.987 | 314 | 2,228 |
| GET /t | 3 | 850 | 3 | 715 | 850 | 856 | 2,150 |
| GET /omnidefence/regions | 60 | 0.698 | 783 | 0.247 | 13.3 | 209 | 1,827 |
| GET /omnitumblr/accounts | — | — | 3 | 75.1 | 1,429 | 1,538 | 1,619 |
| GET /KliveAPI/telemetry/weekly | 10 | 8.93 | 73 | 0.174 | 7.90 | 861 | 1,602 |
| GET /omnitumblr/posts | — | — | 3 | 5.58 | 1,429 | 1,537 | 1,543 |
| GET /omnitumblr/queue | — | — | 3 | 3.32 | 1,429 | 1,536 | 1,540 |
| GET /projects/grandplan | — | — | 17 | 2.79 | 850 | 925 | 1,536 |
| GET /kliveagent/chat/runs | 2 | 0.406 | 12 | 0.247 | 1,395 | 1,395 | 1,399 |
| GET /KliveAPI/telemetry/route | 4 | 31.3 | 23 | 31.6 | 126 | 468 | 1,243 |
| POST /KMProfiles/AttemptLogin | 9 | 0.458 | 31 | 0.415 | 253 | 533 | 1,202 |
| GET /KliveAPI/telemetry/runtime | 6 | 75.1 | 41 | 22.3 | 75.1 | 78.7 | 967 |
| POST /tripwires/update | 2 | 601 | 2 | 212 | 601 | 645 | 865 |
| GET /projects/computers/health | — | — | 52 | 1.40 | 106 | 331 | 863 |
| GET /klivetech/streamables | — | — | 3 | 357 | 425 | 450 | 833 |
| GET /klivegames/servers | 8 | 0.348 | 242 | 0.494 | 0.698 | 568 | 792 |
| GET /projects/get | — | — | 17 | 0.293 | 505 | 533 | 781 |
| GET /omnigram/analytics | 2 | 505 | 3 | 150 | 505 | 546 | 709 |
| GET /KliveCloud/GetDriveInfo | 9 | 0.830 | 28 | 0.415 | 126 | 567 | 701 |
| POST /kliveagent/chat | — | — | 1 | 674 | 674 | 674 | 674 |
| GET /projects/containers | — | — | 51 | 2.35 | 89.3 | 113 | 671 |
| GET /kliveagent/stats/summary | 8 | 0.082 | 25 | 0.087 | 106 | 511 | 634 |
| GET /projects/gates | — | — | 14 | 0.698 | 247 | 247 | 569 |
| GET /KliveAPI/telemetry/trace | 11 | 505 | 19 | 0.494 | 505 | 546 | 568 |
| GET /projects/agents | — | — | 68 | 0.587 | 7.90 | 448 | 566 |
| GET /omnigram/queue | 2 | 505 | 3 | 2.35 | 505 | 547 | 549 |
| GET /projects/observables | — | — | 17 | 1.66 | 425 | 449 | 526 |
| GET /kliveagent/conversations | 1 | 75.1 | 13 | 4.70 | 106 | 106 | 514 |
| GET /KMProfiles/GetAllProfiles | — | — | 27 | 0.494 | 75.1 | 131 | 489 |
| GET /projects/councils | — | — | 17 | 1.66 | 425 | 448 | 487 |
| GET /KliveCloud/ListItems | 1 | 18.8 | 3 | 212 | 241 | 241 | 464 |
| GET /KliveCloud/GetItemInfo | 29 | 0.349 | 87 | 0.293 | 0.587 | 313 | 458 |
| GET /klivetech/firmware/projects | — | — | 3 | 2.79 | 425 | 448 | 451 |
| GET /projects/events | — | — | 14 | 1.17 | 234 | 234 | 451 |
| GET /klivetech/firmware/config | — | — | 3 | 2.79 | 425 | 448 | 450 |
| GET /klivetech/firmware/jobs | — | — | 3 | 2.35 | 425 | 447 | 450 |
| GET /omnitumblr/dashboard-stats | 10 | 0.328 | 32 | 0.052 | 106 | 337 | 445 |
| GET /klivetech/GetAllGadgets | 8 | 0.031 | 28 | 0.026 | 89.3 | 341 | 437 |
| GET /klivelink/agents | 9 | 0.236 | 28 | 0.031 | 106 | 222 | 406 |
| GET /admin/terminal/status | — | — | 11 | 37.6 | 83.5 | 83.5 | 397 |
| GET /omnigram/dashboard-stats | 12 | 0.293 | 32 | 0.087 | 75.1 | 295 | 373 |
| GET /ping | 11 | 0.340 | 676 | 0.207 | 1.17 | 19.4 | 325 |
| GET /kliveagent/status | 10 | 9.36 | 41 | 0.026 | 7.90 | 291 | 322 |
| GET /omniscience/deduction/status | — | — | 1 | 300 | 300 | 304 | 304 |
| GET /kliveagent/tasks | 1 | 75.1 | 13 | 0.494 | 105 | 105 | 274 |
| GET /projects/activity | — | — | 14 | 0.207 | 232 | 232 | 270 |
| GET /klivechat/rooms | — | — | 8 | 0.247 | 253 | 257 | 261 |
| GET /KliveCloud/ListShareLinks | 1 | 0.346 | 3 | 89.3 | 126 | 137 | 227 |
| GET /omniscience/radar/alerts | — | — | 1 | 212 | 212 | 227 | 227 |
| GET /api/logs/summary | 9 | 8.87 | 27 | 3.32 | 22.3 | 63.7 | 192 |
| GET /projects/ledger | — | — | 17 | 0.494 | 150 | 162 | 180 |
| GET /projects/digest | — | — | 17 | 0.698 | 150 | 163 | 175 |
| GET /KMProfiles/LoginStatus | 84 | 0.349 | 386 | 0.174 | 0.415 | 31.1 | 165 |
| GET /kliveagent/jobs | 10 | 0.349 | 40 | 0.044 | 6.64 | 91.1 | 148 |
| GET /seleniumManager/getAllSeleniumInstances | — | — | 57 | 0.207 | 5.58 | 90.9 | 129 |
| GET /stratum/projects | — | — | 1 | 75.1 | 75.1 | 76.4 | 76 |
| GET /kliveagent/conversations/get | 1 | 10.4 | 8 | 0.987 | 44.7 | 45.9 | 73 |
| GET /KliveMultiTool/tools | — | — | 2 | 0.349 | 49.6 | 49.6 | 50 |
| GET /omnidefence/request | — | — | 2 | 0.698 | 26.5 | 26.5 | 27 |
| GET /omnidefence/fingerprint/status | — | — | 4 | 2.35 | 18.8 | 19.1 | 26 |
| GET /omnidefence/fp/nonce | 8 | 9.39 | 30 | 0.247 | 3.32 | 9.50 | 23 |
| GET /kliveagent/chat/pending | — | — | 40 | 0.293 | 1.97 | 2.95 | 21 |
| GET /KliveAPI/telemetry/health | 8 | 11.2 | 39 | 0.174 | 0.415 | 11.2 | 18 |
| GET /timemanager/getalltasks | — | — | 1 | 13.3 | 13.3 | 14.3 | 14 |
| GET /omnigram/posts | 2 | 5.22 | 3 | 5.37 | 5.37 | 5.37 | 11 |
| GET /cs2arbitragebot/latestliquidityplan | 3 | 7.28 | 3 | 2.35 | 7.28 | 7.28 | 10 |
| GET /tripwires/get | 6 | 1.97 | 7 | 0.830 | 1.97 | 2.07 | 8 |
| GET /omnitumblr/events | — | — | 3 | 3.32 | 3.84 | 3.84 | 8 |
| GET /api/omnitrader/status | 2 | 3.81 | 4 | 0.147 | 3.81 | 3.81 | 7 |
| GET /omnigram/events | 2 | 2.63 | 3 | 2.79 | 3.74 | 3.74 | 7 |
| GET /omnidefence/classes | 3 | 0.470 | 8 | 0.349 | 3.80 | 3.80 | 6 |
| GET /omniscience/targets/suggestions | — | — | 1 | 5.58 | 5.58 | 6.04 | 6 |
| GET /omnigram/accounts | 2 | 2.76 | 3 | 2.35 | 2.76 | 2.76 | 6 |
| GET /klivechat/me | — | — | 3 | 1.97 | 2.30 | 2.30 | 4 |
| GET /omniscience/schedule/status | — | — | 1 | 4.40 | 4.40 | 4.40 | 4 |
| GET /KliveAPI/telemetry/rum | 6 | 0.247 | 19 | 0.174 | 0.587 | 0.602 | 4 |
| GET /robots.txt | 5 | 0.415 | 13 | 0.247 | 1.17 | 1.26 | 4 |
| GET /KliveCloud/GetSharedItemInfo | — | — | 2 | 0.293 | 3.24 | 3.24 | 4 |
| POST /omnidefence/ip/reclassify | — | — | 1 | 1.97 | 1.97 | 2.07 | 2 |
| GET /KliveAPI/telemetry/live | 3 | 0.987 | 3 | 0.293 | 0.987 | 1.02 | 2 |
| POST / | — | — | 3 | 0.293 | 0.293 | 0.308 | 1 |
| GET /omniscience/sources | — | — | 1 | 0.494 | 0.494 | 0.531 | 1 |

Source: [24h](routes-24h.json), [7d](routes-7d.json),
[30d](routes-30d.json). The 30d preset was 0.4 minutes old at collection
time. The table uses the seven-day view to represent recent traffic.
