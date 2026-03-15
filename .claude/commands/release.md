---
description: バージョンタグを付けて GitHub Release を作成する
argument-hint: バージョン番号（例: 3.3.0）省略時は自動インクリメント
allowed-tools: Bash, Read, Write, Edit, Grep, Glob
---

バージョンタグを付けて GitHub Release を作成する。
バイナリビルドは CI（`.github/workflows/release.yml`）が自動で行う。

## Phase 1: 準備

1. `git status` で未コミットの変更がないか確認（あればユーザーに報告して停止）
2. タグ確認:
   - `git tag -l --sort=-v:refname` でローカルタグ一覧
3. 既存リリース確認: `gh release list`
4. バージョン番号を決定:
   - 引数ありならそれを使用（`v` prefix 付与: `3.3.0` → `v3.3.0`）
   - 引数なしなら最新タグから変更規模に応じてバージョンを提案
   - タグが無ければ `v0.1.0` から開始
5. 前回タグからの変更一覧を `git log --oneline <前回タグ>..HEAD` で取得
6. リリースノートを生成（英語、カテゴリ分け: Features / Fixes / Other）
7. バージョン番号とリリースノートをユーザーに提示

→ **承認待ち**

## Phase 1.5: README 更新検討

1. README.md / README.ja.md を読み、現在の実装と乖離がないか確認:
   - インストール手順（ワンライナー、ソースビルド）
   - コマンドラインオプション
   - 機能一覧
   - 設定項目
2. 更新が必要な場合はユーザーに報告し、承認後に修正する
3. 更新不要の場合はスキップして Phase 2 へ進む

## Phase 2: リリース

1. `SshAgentProxy.csproj` の `<Version>` を新バージョン番号に更新（`v` prefix なし）
2. バージョン更新をコミット＆プッシュ（README 更新があればそれも含める）:
   ```
   git add SshAgentProxy.csproj
   git commit -m "chore: bump version to v<version>"
   git push
   ```
3. `gh release create <version> --title "<version>" --notes "<リリースノート>"` で GitHub Release を作成（タグも自動作成される）
4. CI がトリガーされたことを確認: `gh run list --limit 1`
5. CI 完了を待つ: `gh run watch <run_id>`
6. リリースにバイナリが添付されたことを確認: `gh release view <version>`
7. 結果を報告（タグ名 + リリースURL + 含まれるコミット数 + CI ステータス）

## ルール

- 未コミットの変更がある場合は Phase 1 で停止する
- 既存タグと重複する場合はエラーで止まる
- リリースノートは英語で書く (Categories: Features / Fixes / Other)
- `gh` コマンド失敗時はエラー内容を報告して止まる
- バイナリビルドはローカルで行わない（CI に任せる）

## 完了条件

- [ ] SshAgentProxy.csproj の Version が更新・コミットされている
- [ ] GitHub Release が作成された
- [ ] CI が成功し、Windows バイナリ (SshAgentProxy-win-x64.zip) が添付されている
- [ ] リリースURLが報告された
