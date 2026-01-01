# SSH Agent Proxy

[![CI](https://github.com/jss826/SshAgentProxy/actions/workflows/ci.yml/badge.svg)](https://github.com/jss826/SshAgentProxy/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![Release](https://img.shields.io/github/v/release/jss826/SshAgentProxy)](https://github.com/jss826/SshAgentProxy/releases)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)

[English README](README.md)

Windows用のSSHエージェントプロキシ。要求されたキーに応じて **1Password** と **Bitwarden** のSSHエージェントを自動的に切り替えます。

## 課題

Windowsでは、1PasswordとBitwardenは両方とも同じ名前付きパイプ（`\\.\pipe\openssh-ssh-agent`）をSSHエージェントに使用します。このパイプ名はハードコードされており、変更することはできません。一度にパイプを所有できるアプリケーションは1つだけであり、どのアプリケーションが現在所有しているかを判別するAPIはありません—確認する唯一の方法はパイプに問い合わせること（スキャンまたは署名）です。

両方のアプリケーションにSSHキーを保存している場合、手動で切り替える必要があります（一方のアプリを閉じて、もう一方を開く）。

## 解決策

SSH Agent Proxyは独自の名前付きパイプ（`\\.\pipe\ssh-agent-proxy`）を作成し、プロキシとして機能します。SSH操作が要求されると：

1. **キー一覧取得**: 1PasswordとBitwarden両方のキーをマージして返す
2. **署名**: 要求されたキーを所有する正しいエージェントに自動切り替え

## 機能

- キーのフィンガープリントに基づく自動エージェント切り替え
- 設定されたすべてのエージェントからのキー一覧のマージ
- `SSH_AUTH_SOCK` 環境変数の自動設定（終了時に復元）
- 初期設定後は手動操作不要
- キーとエージェントのマッピングを保存して次回以降の動作を高速化
- 既存のパイプから現在のエージェントを検出してエージェント再起動を最小化
- 失敗のキャッシュで無駄な再試行を回避
- 任意の数のSSHエージェントに対応（1Password/Bitwardenに限定されない）

## 必要要件

- Windows 10/11
- .NET 10.0 ランタイム
- SSHエージェントを有効にした1Password
- SSHエージェントを有効にしたBitwarden（オプション）

## インストール

### ソースから

1. リポジトリをクローン：
   ```
   git clone https://github.com/jss826/SshAgentProxy.git
   cd SshAgentProxy
   ```

2. ビルドして発行：
   ```
   dotnet publish -c Release -o ./publish
   ```

3. プロキシを実行：
   ```
   ./publish/SshAgentProxy.exe
   ```

任意で、`publish`フォルダをPATHに追加するか、実行ファイルを便利な場所にコピーしてください。

プロキシはユーザー環境変数に `SSH_AUTH_SOCK` を自動設定します。新しいターミナルウィンドウは自動的にプロキシを使用します。

## 使い方

### プロキシの起動

アプリケーションを実行するだけ：

```
SshAgentProxy.exe
```

起動時に：
- ユーザー環境変数に `SSH_AUTH_SOCK=\\.\pipe\ssh-agent-proxy` を設定（終了時に復元）
- `%APPDATA%\SshAgentProxy\config.json` に設定ファイルを作成（なければ）
- SSHエージェントリクエストの待ち受けを開始

### 対話コマンド

実行中は以下のキーボードショートカットが使えます：
- `1` - 1Passwordに切り替え
- `2` - Bitwardenに切り替え
- `r` - 現在のエージェントからキーを再スキャン
- `q` - 終了

### コマンドラインオプション

```
SshAgentProxy.exe [オプション]

オプション：
  (なし)        プロキシサーバーを起動
  --uninstall   ユーザー環境変数からSSH_AUTH_SOCKを削除
  --reset       --uninstallと同じ
  --help, -h    ヘルプを表示
```

### アンインストール

プロキシは正常終了時に `SSH_AUTH_SOCK` を元の値に自動的に復元します。完全に削除するには：

```
SshAgentProxy.exe --uninstall
```

これによりユーザー環境変数から `SSH_AUTH_SOCK` が削除されます。新しいターミナルはデフォルトのWindows OpenSSHエージェントを使用します。

## 設定

設定ファイルは `%APPDATA%\SshAgentProxy\config.json` にあります：

```json
{
  "proxyPipeName": "ssh-agent-proxy",
  "backendPipeName": "openssh-ssh-agent",
  "agents": {
    "1Password": {
      "processName": "1Password",
      "exePath": "C:\\Users\\...\\AppData\\Local\\1Password\\app\\8\\1Password.exe",
      "priority": 1
    },
    "Bitwarden": {
      "processName": "Bitwarden",
      "exePath": "C:\\Users\\...\\AppData\\Local\\Programs\\Bitwarden\\Bitwarden.exe",
      "priority": 2
    }
  },
  "keyMappings": [],
  "defaultAgent": "1Password",
  "failureCacheTtlSeconds": 60
}
```

### エージェントの追加

Windows名前付きパイプインターフェースを使用する任意のSSHエージェントを追加できます：

```json
{
  "agents": {
    "1Password": { "processName": "1Password", "exePath": "...", "priority": 1 },
    "Bitwarden": { "processName": "Bitwarden", "exePath": "...", "priority": 2 },
    "KeePassXC": { "processName": "KeePassXC", "exePath": "...", "priority": 3 }
  }
}
```

`priority` フィールドはキー検索時にエージェントを試行する順序を決定します（小さい数値 = 高い優先度）。

### キーマッピング

キーマッピングは署名操作が成功すると自動的に保存されます。事前設定も可能です：

```json
{
  "keyMappings": [
    { "fingerprint": "A1B2C3D4E5F6...", "agent": "1Password" },
    { "comment": "work@company.com", "agent": "Bitwarden" }
  ]
}
```

プロキシはこれらのマッピングを以下の目的で使用します：
1. 起動時にバックエンドパイプを現在所有しているエージェントを検出
2. 試行錯誤なしで署名リクエストを正しいエージェントに直接ルーティング

## 動作の仕組み

1. **プロキシパイプ**: SSHクライアント用に `\\.\pipe\ssh-agent-proxy` を作成
2. **バックエンドパイプ**: リクエストを `\\.\pipe\openssh-ssh-agent`（アクティブなエージェントが所有）に転送
3. **キー検出**: 最初のID要求時にエージェントをスキャンして完全なキーリストを構築（将来の使用のためにキャッシュ）
4. **スマートルーティング**: 署名時にキーを所有するエージェントを確認し、必要に応じて切り替え
5. **キーキャッシュ**: 再スキャンなしで即時起動するためにキーデータを設定に保存

### エージェント切り替えフロー

```
SSHクライアント → プロキシ → キーマッピング確認 → 必要に応じてエージェント切り替え → 認証待機 → 署名 → レスポンス
```

エージェント切り替え時：
1. 現在のエージェントプロセスを終了（パイプを解放）
2. ターゲットエージェントを起動（パイプを取得）
3. 必要に応じてユーザー認証を待機（最大約15秒）

### 用語

- **スキャン（キー一覧取得）**: 利用可能なキーの一覧を要求する操作（`ssh-add -l`）。プロキシはこれを使用してキーを発見し、パイプの所有者を検出します。
- **署名**: SSH接続時にキーで認証する操作（`ssh user@host`）。対象のエージェントがパイプを所有し、ロック解除されている必要があります。

### エージェントの挙動の違い

プロキシの戦略は、1PasswordとBitwardenの挙動の違いに基づいて最適化されています：

| 挙動 | 1Password | Bitwarden |
|------|-----------|-----------|
| ロック中のキー一覧取得 | キーを返す | ロック解除が必要 |
| 起動時のパイプ取得 | 空いていれば取得 | 使用中でも奪取 |
| 他エージェント終了後 | 自動取得しない | 自動取得しない |

**影響：**

- **Bitwardenのロック解除プロンプト**: Bitwardenへの問い合わせ（キー一覧取得でも）はロック解除を要求します。プロキシはキャッシュされたマッピングとプロセス検出を使用し、パイプへの問い合わせを最小限にします。
- **パイプ所有者の検出**: パイプに問い合わせる代わりに（Bitwardenのロック解除をトリガーするため）、プロキシはプロセス状態から所有者を推測します：
  - 両方実行中 → Bitwardenがパイプを所有（起動時に奪取するため）
  - 1Passwordのみ実行中 → 軽量スキャンでパイプを確認（1Passwordはロック解除なしで応答）；応答がなければパイプは孤立状態の可能性
  - Bitwardenのみ実行中 → Bitwardenがパイプを所有
  - どちらも未実行 → 誰もパイプを所有していない
- **起動時の最適化**: キーマッピングが既に2つ以上の異なるエージェントを参照している場合、プロキシは不要なBitwardenロック解除プロンプトを避けるため初期スキャンをスキップします。代わりにキャッシュデータを使用します。未知のキーへの署名要求があった場合、必要に応じてエージェントをスキャンします。手動で再スキャンするには `r` を押してください。

## トラブルシューティング

### SSH操作がハングまたは失敗する

1. プロキシが実行中か確認
2. `SSH_AUTH_SOCK` が正しく設定されているか確認: `echo $env:SSH_AUTH_SOCK`
3. プロキシを再起動してみる

### キーが表示されない

1. 1Password/BitwardenでSSHエージェントが有効になっているか確認
2. プロキシで `r` を押してキーを再スキャン
3. アプリケーションにSSHキーが設定されているか確認

### Permission denied

通常、キーが別のエージェントにあることを意味します。プロキシは自動的に切り替えるはずですが、失敗した場合：
1. プロキシのログでエラーを確認
2. `1` または `2` キーで手動切り替え
3. ターゲットアプリケーションにキーが存在するか確認

## 既知の制限事項

以下のエッジケースは対応しておらず、手動での対処が必要な場合があります：

- **古いパイプ**: エージェントがクラッシュすると、名前付きパイプは残るが機能しなくなることがあります。プロキシを再起動して解決してください。
- **キーのローテーション**: 同じフィンガープリントでキーを削除/再作成すると、`config.json` の古いマッピングがルーティング問題を引き起こす可能性があります。`keyMappings` 配列を手動で編集して古いエントリを削除してください。
- **マルチキー操作**: 単一のSSH操作で複数のエージェントのキーが必要な場合（まれ）、最初のキーのエージェントのみが使用されます。
- **ロックされたVault**: エージェントのVaultがロックされている場合、キー一覧が空になることがあります。Vaultをアンロックして `r` を押してキーを再スキャンしてください。
- **複数エージェントでの初回スキャン**: 初回スキャン時、あるエージェントが既にパイプを保持している場合、他のエージェントのキーは署名に使用されるまで検出されないことがあります。プロキシは時間とともにキーとエージェントのマッピングを学習します。

## ライセンス

MIT License - 詳細は [LICENSE](LICENSE) ファイルを参照してください。
