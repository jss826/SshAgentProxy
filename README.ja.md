# SSH Agent Proxy

[![CI](https://github.com/jss826/SshAgentProxy/actions/workflows/ci.yml/badge.svg)](https://github.com/jss826/SshAgentProxy/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![Release](https://img.shields.io/github/v/release/jss826/SshAgentProxy)](https://github.com/jss826/SshAgentProxy/releases)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)

[English README](README.md)

Windows用のSSHエージェントプロキシ。要求されたキーに応じて **1Password** と **Bitwarden** のSSHエージェントを自動的に切り替えます。

## 課題

Windowsでは、`\\.\pipe\openssh-ssh-agent` という名前付きパイプを所有できるアプリケーションは一度に1つだけです。1PasswordとBitwardenの両方にSSHキーを保存している場合、手動で切り替える必要があります（一方のアプリを閉じて、もう一方を開く）。

## 解決策

SSH Agent Proxyは独自の名前付きパイプ（`\\.\pipe\ssh-agent-proxy`）を作成し、プロキシとして機能します。SSH操作が要求されると：

1. **キー一覧取得**: 1PasswordとBitwarden両方のキーをマージして返す
2. **署名**: 要求されたキーを所有する正しいエージェントに自動切り替え

## 機能

- キーのフィンガープリントに基づく自動エージェント切り替え
- 両エージェントからのキー一覧のマージ
- `SSH_AUTH_SOCK` 環境変数の自動設定
- 初期設定後は手動操作不要
- キーとエージェントのマッピングを保存して次回以降の動作を高速化

## 必要要件

- Windows 10/11
- .NET 10.0 ランタイム
- SSHエージェントを有効にした1Password
- SSHエージェントを有効にしたBitwarden（オプション）

## インストール

1. リポジトリをクローン：
   ```
   git clone https://github.com/jss826/SshAgentProxy.git
   ```

2. プロジェクトをビルド：
   ```
   dotnet build
   ```

3. プロキシを実行：
   ```
   dotnet run
   ```

プロキシはユーザー環境変数に `SSH_AUTH_SOCK` を自動設定します。新しいターミナルウィンドウは自動的にプロキシを使用します。

## 使い方

### プロキシの起動

アプリケーションを実行するだけ：

```
SshAgentProxy.exe
```

初回実行時に：
- ユーザー環境変数に `SSH_AUTH_SOCK=\\.\pipe\ssh-agent-proxy` を設定
- `%APPDATA%\SshAgentProxy\config.json` に設定ファイルを作成
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

プロキシを削除してデフォルトのSSHエージェント動作に戻すには：

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
    "onePassword": {
      "name": "1Password",
      "processName": "1Password",
      "exePath": "C:\\Users\\...\\AppData\\Local\\1Password\\app\\8\\1Password.exe"
    },
    "bitwarden": {
      "name": "Bitwarden",
      "processName": "Bitwarden",
      "exePath": "C:\\Users\\...\\AppData\\Local\\Programs\\Bitwarden\\Bitwarden.exe"
    }
  },
  "keyMappings": [],
  "defaultAgent": "1Password"
}
```

### キーマッピング

キーとエージェントのマッピングを事前設定できます：

```json
{
  "keyMappings": [
    { "fingerprint": "A1B2C3D4E5F6...", "agent": "1Password" },
    { "comment": "work@company.com", "agent": "Bitwarden" }
  ]
}
```

## 動作の仕組み

1. **プロキシパイプ**: SSHクライアント用に `\\.\pipe\ssh-agent-proxy` を作成
2. **バックエンドパイプ**: リクエストを `\\.\pipe\openssh-ssh-agent`（アクティブなエージェントが所有）に転送
3. **キー検出**: 最初のID要求時に両エージェントをスキャンして完全なキーリストを構築
4. **スマートルーティング**: 署名時にキーを所有するエージェントを確認し、必要に応じて切り替え
5. **プロセス管理**: PowerShell CIMを使用してセッション間でプロセスを終了し、エージェントを切り替え
6. **両エージェント起動**: 署名後、セカンダリエージェントを自動起動して両方を利用可能に

### エージェント切り替えフロー

```
SSHクライアント → プロキシ → キー所有者確認 → 必要に応じてエージェント切り替え → バックエンドエージェント → 署名 → レスポンス
```

エージェント切り替え時：
1. 両エージェントプロセスを終了（パイプを解放）
2. ターゲットエージェントを起動（パイプを取得）
3. オプションでセカンダリエージェントを起動（パイプ所有権には影響なし）

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

## ライセンス

MIT License - 詳細は [LICENSE](LICENSE) ファイルを参照してください。
