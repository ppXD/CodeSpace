# 節點動效實施進度 — 實時追蹤

> 計劃:節點運行時畫布設計 + 伴隨面板 v2(owner-locked 2026-07-13)· model claude-opus-4-8
> worktree 慣例 `../CodeSpace-<suffix>`,完工提交後 `git worktree remove`(分支保留)
> 提交規則:feature 分支本地提交(不 push、不建 PR;E2E 分支經 owner 授權已 push)· 無 AI co-author 尾註(Rule 15)
> 圖例:✅ 驗證+提交 · 🧹 worktree 已清 · ⏳ 進行中

## M1 · 基座 ✅ 完成(7 PR + 3 閘門全過)
| PR | 分支 | 提交 | 驗證 |
|----|------|------|------|
| A1 motion tokens(合成器 ring::after) | feat/canvas-motion-tokens | 3d353239 | build ✓ |
| A2 useNowTick clock | feat/now-tick | e4cf5265 | 9 + 全套 1349 ✓ |
| A3 RunCanvas 節點身份穩定(patchNodes) | feat/run-node-identity | 38db255d | patch 7/7 ✓ |
| A4 SSE RunLiveStore | feat/run-live-store | cc511027 | fold 11 + hook 3 ✓ |
| A5 spotlight 上卡 | feat/node-spotlight | bb5c341f | 14 + WorkflowNode 29 ✓ |
| A6 footer registry | feat/footer-registry | 7b891d3f | registry 46 + WorkflowNode 13 ✓ |
| BE-1 x-spotlight manifests | feat/manifest-spotlight | 6be60640 | 12 spotlight + 127 manifest + dotnet ✓ |

整合 `m1/integration-check` 545b7cad(零衝突,1432 測試綠)。**閘門全過**(headless Chrome 截圖 tmp/designs/m1-gate-{canvas,nodes}.png + RunCanvas.rerender render-count 測試 + wfGlow opacity-only 結構證明)。

## M2 · footer 活體 + 真實大模型 E2E ✅ 完成
| PR | 分支 | 提交 | 驗證 |
|----|------|------|------|
| wire useRunLive→RunCanvas + null-safe useNodeLiveContext | feat/run-live-wire | 0915a293 | build ✓ |
| B1 externalCall footer | feat/footer-external-call | 30e14c5b | 24 + 全套 1454 ✓ |
| B5 wait footer(inline 批准接 useResumeRun) | feat/footer-wait | 48e525fc | 11 + 全套 1444 ✓ |
| B4 branch-dots footer | feat/footer-branch-dots | 4e5b9e65 | 6 + 全套 1439 ✓ |
| **RealModel footer-signals E2E** | feat/footer-signals-e2e | fa42537f | dotnet 0 err + skip 2/2 ✓ |

整合 `m2/integration-check` 713f0655(1471 測試綠)。

## M3 · tokenStream + agentFeed footer ✅ 完成
| PR | 分支 | 提交 | 驗證 |
|----|------|------|------|
| B2 tokenStream footer | feat/footer-token-stream | 05288f9a | 10 + 全套 1481 ✓ |
| B3 agentFeed footer(旗艦) | feat/footer-agent-feed | 17ef6629 | 12 測試 ✓(standalone 綠核實 27) |

整合 `m3/integration-check` 1da84ecb(1493 測試綠)。**跨 PR reconciliation**:agent.run→AgentFeedFooter、llm.complete→TokenStreamFooter 後,共用 WorkflowNode 測試更新(補 mock、泛用 bar 改 receipt-routed trigger、parked 斷言改查 `.wf-rf-feed-title`)。
> B3 發現:inline 工具批准接不上 —— governed 核准端點後端不存在(後端真實缺口),誠實給 affordance + TODO。

## M4 · 邊 + 容器 + 一拍系 ✅ 完成
**批次 1**:C1 邊文法 `73e1860f`(taken/dead + 修 error/catch 終態永動 bug + hover;verdict defer B7)· B6 pipeline footer `d4d7743f`(git.integrate 三態 + run_command exit;Conflicted/非零 exit=amber)。整合 `m4/integration-check` 7db3ec4e(1516 測試綠)。
**批次 2**:B7 一拍系 `f2116910`(trigger/terminal/verdict + trigger digest;no-replay-on-mount 經測試驗證)· C2 container header `35613eb0`(map/loop 計數;try routingHints defer)· C3 animation budget `855f7dac`(≤2 hot 節點才全脈衝)。整合 `m4b2/integration-check` a66f401b(**1552 測試綠**;解 WorkflowNode data-hot×data-beat + 測試 describe 衝突)。所有 worktree 已清。

**footer 型態:8 種已 7 種真實實作**(receipt·externalCall·wait·branchDots·tokenStream·agentFeed·pipeline;verdict=B7 一拍非常駐 footer)。
> C2 發現:容器 `d.runRows` 是自己的 top-level 列非扇出 body 列 → header 計數在真實畫布 graceful degrade 到無 meta,需 sibling PR 聚合子節點列(follow-up,C2 已加 regression 測試)。

## 真實大模型 CI ✅✅ 兩測試皆真實 PASS(run 29256034881)
- **`real model (run-canvas footer live-signals)` job = SUCCESS**;整體 run failure = 無關的既有 supervisor/session/planner real-model 測試(常見 flaky/gateway)。
- **Test 1(B1+B2)· Passed 19s**:log 明證「真實 Anthropic 模型記錄了 footer 的 B1 external_call 對(target=anthropic:*, method=complete)+ B2 串流 interaction feed(started + 3 interaction.delta 列、單調 ordinal、completed usage.outputTokens=203),全歸於 kind 'llm.complete'」。
- **Test 2(B3)· Passed 1m16s**:log 明證「真實 claude agent.run 產生 footer 的 B3 事件 feed —— 14 個 tool/file/command 事件[CommandExecuted,FileChanged]+ 3 assistant 訊息 + terminal Completed」。
- **結論:footer 活體訊號的資料源(external_call / interaction.delta / agent_run_event)確由真實大模型 + 真實 claude CLI 產生 —— 非 gateway skip,是真 pass。**

## 待辦 / polish
- B1:node config 未上卡 → http.request timeout 環暫省;digest `<a>` 嵌 `<button>` HTML 巢狀待清。
- 視覺:各 footer 活體需 :5180 跑真實 run 覆核(post-merge cadence)。
- 剩餘計劃:D 系伴隨面板(D1 pane / D2 follow-pin / D3 雙向跳轉 / D4 除役 Trace modal / D5 mini-tabs)+ M6 收口(量測協議 + chaos)。

## 事件日誌
- M1 批次 1(A1/A2/A3/A6/BE-1)→ 批次 2(A4/A5)→ 整合 1432 綠 → 3 閘門過
- M2 wire → B1/B5/B4 + E2E → 整合 1471 綠;E2E push + CI dispatch
- M3 B2/B3 → 整合 1493 綠(reconcile B2×B3 交互)
- M4 批次 1 C1/B6 → 整合 1516 綠;批次 2 B7/C2/C3 進行中
- CI footer-signals SUCCESS

## D 系 · 伴隨面板(設計主線;順序疊加 —— 都改 SessionRoomView,各 PR 從前一個分支)
| PR | 分支 | 基底 | 狀態 | 提交 |
|----|------|------|------|------|
| D1 companion pane(右側停靠 RunCanvas + EXECUTION 卡召喚 + resize + composer 常駐 + ?pane URL;modal 未動) | feat/room-run-pane | m4b2 | ✅🧹 | 8e57036a(15 測試 + 全套 1561 + build ✓)|
| D5 pane mini-tabs(畫布/變更/紀錄) | feat/pane-mini-tabs | feat/room-run-pane | ✅🧹 | f9bab3da(17 測試 + 全套 1566 + build ✓)|
| D2 follow/pin 綁定(follow 自動 rebind + 召喚 auto-pin + jump chip) | feat/pane-turn-binding | feat/pane-mini-tabs | ✅🧹 | c965ae75(23 + route 10 + 全套 1579 ✓;左欄收合 defer)|
| D3 journal↔canvas 雙向跳轉(前向 ?node 聚焦+beat 卡跳轉 shipped;反向 defer 避免耦合泛用 WorkflowNode;phase-click defer 因 ExecutionMapStep 無 nodeId) | feat/journal-canvas-jumps | feat/pane-turn-binding | ✅🧹 | f6bc15f7(42 + 全套 1588 ✓)|
| D4 **除役 Trace modal**(scoped:Room modal 移除 + 深鏈改寫永不 404 + 子 run 已導航 + "← 上層 run" breadcrumb) | feat/remove-trace-modal | feat/journal-canvas-jumps | ✅🧹 | 20ac701d(1595 測試 + build ✓)|
> D4 調查:RunViewerDialog 有 4 渲染處,只 1 是 Room(其餘 = 編輯器/runs-index/agent-detail 仍需)→ 正確 scope 到只除役 Room 用法、保留共用組件、標記殘留(全刪需先遷移編輯器等 surface)。TurnActions「開啟畫布」/ ErrorCard「查看紀錄」/ header terminal icon 移除。session-less fallback → RunDetailView 全頁(誠實無回歸)。

## 🏁 實作完整 tip:`impl/complete-tip` = `20ac701d`
= M1–M4(節點活體 + 邊/容器/一拍)+ D1–D5(伴隨面板 + Trace modal 除役),線性疊加、全套 **1595 測試綠** + build。**整條實施計劃基本完成**;所有 worktree 已清,分支全保留。
D1 偏差:自寫 fraction-based `useRoomPaneFrac`(非改編輯器關鍵的 px-based use-pane-resize)+ RunCanvas props 複製 RunDetailView 組裝。順序鐵律:parity(D1/D5)→ 綁定跳轉(D2/D3)→ 刪碼(D4 最後,全程 modal 可用)。
