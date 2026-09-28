> v0.5.0の現行導入は[Windowsソースビルド](WINDOWS-LOCAL.md)です。この文書はコード署名証明書取得後に再開するバイナリ配布用で、現時点では使用しません。

# Windowsコード署名対応（未完了）

v0.4.0はGonioLauncher.dllがNotSignedのため、ユーザーのWindowsのVerifiedAndReputableDesktopポリシーで拒否された（CodeIntegrity 3077/3089、0x800711c7）。Windowsの設定変更による回避は実施しない。

更新manifestのRSA署名は自動更新クライアントの改ざん検出用。WindowsのAuthenticode署名とは別であり、代用できない。

## 必要なもの

公開信頼された認証局のRSAコード署名証明書、または利用資格を満たすMicrosoft Artifact SigningのPublic Trustプロファイル。現時点でユーザーは未保有。契約・審査・費用は別途必要。自己署名証明書を信頼ルートへ追加する方法は採用しない。

## 証明書取得後

1. 新しいバージョン番号でWindows用にpublishする。
2. Windows SDKのSignToolを用意し、CurrentUser/Myの証明書（トークン/HSMによる鍵管理を含む）を使う場合は次を実行する。秘密鍵をGitや配布ZIPに含めない。

```powershell
.\scripts\sign-windows.ps1 -CertificateThumbprint '<証明書の拇印>' -TimestampUrl '<認証局が指定するRFC3161 URL>' -SignTool '<signtool.exeのパス>'
```

Artifact Signingを使う場合はその公式ツールで同じ4ファイルを署名した後、VerifyOnlyで検証する。このスクリプトはArtifact Signingの契約や認証を自動設定しない。

対象: GonioWeb.exe / GonioWeb.dll / launcher/GonioLauncher.exe / launcher/GonioLauncher.dll。Microsoftランタイムの署名は保持し、全EXE/DLLを検証する。

3. Windowsでpackage-windows.pyを実行する。ZIP作成前に全EXE/DLLのAuthenticode検証を実行し、不備があれば停止する。ZIPとmanifestはコード署名後に生成する。
4. Smart App Control有効のWindowsでインストール・起動・更新切替を確認する。ローカルの署名検証だけで任意の管理ポリシーでの実行を保証しない。
5. 既存のv0.4.0を上書きせず、新しいバージョンとして公開する。v0.4.0はランチャー自体がブロックされているので、初回修正版は手動ダウンロード・導入が必要。既存インストールの復旧・ランチャー交換手順も実機検証する。

## 確認状態

署名・検証スクリプトとパッケージ作成の必須チェックを追加。Macで未署名の配布版作成が停止することを確認。署名証明書未取得、Windows上でのスクリプト実行・署名・修正版の動作確認は未実施。

## 公式資料

- https://learn.microsoft.com/en-us/windows/apps/develop/smart-app-control/code-signing-for-smart-app-control
- https://learn.microsoft.com/en-us/windows/win32/seccrypto/signtool
- https://learn.microsoft.com/en-us/azure/artifact-signing/quickstart
