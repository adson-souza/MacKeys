# Third-party notices

<sub>English · [Português (Brasil)](#avisos-de-terceiros-português-do-brasil)</sub>

## QuickLook

MacKeys' Quick Look is **QuickLook**.

- Project: https://github.com/QL-Win/QuickLook
- Authors: Paddy Xu and the QL-Win contributors (Copyright © 2017-2026 QL-Win Contributors)
- License: GNU General Public License, version 3 or later (GPL-3.0). Full text: https://github.com/QL-Win/QuickLook/blob/master/LICENSE and https://www.gnu.org/licenses/gpl-3.0.html
- Version used: the latest official portable release, checked against the SHA-256 GitHub publishes for each file. If GitHub doesn't answer, MacKeys installs 4.5.0:
  - `QuickLook-4.5.0.7z`, SHA-256 `d1d578deb75b83ea09255b89382b088d35d38125c0c2b309825b1e20ac962b91`
  - `QuickLook-4.5.0.zip` (fallback), SHA-256 `852d8bcccd984e416fc8491ccedb848f0c3472e930ee0d292188b2ea3df524e0`
- Source code of each version: https://github.com/QL-Win/QuickLook/tags

MacKeys **neither includes nor modifies** QuickLook's code or binaries. During installation (or from the button in Settings › Quick Look), MacKeys downloads the official archive from the project's GitHub releases, checks its SHA-256 and extracts it unchanged into `%LOCALAPPDATA%\Programs\MacKeys\QuickLook`. The two programs run as separate processes: MacKeys only starts and stops QuickLook and writes preferences into QuickLook's own settings file: `ShowTrayIcon = False` (hides its tray icon), `DisableAutoUpdateCheck = True` (MacKeys handles updates) and the options you change in Settings › Quick Look.

## Animated Fluent Emojis

The animated icons in the README come from https://github.com/Tarikul-Islam-Anik/Animated-Fluent-Emojis, based on Microsoft's Fluent Emoji (https://github.com/microsoft/fluentui-emoji, MIT license). They only appear on the repository page and are not part of the app.

---

## Avisos de terceiros (português do Brasil)

### QuickLook

O Quick Look do MacKeys é o **QuickLook**.

- Projeto: https://github.com/QL-Win/QuickLook
- Autores: Paddy Xu e os colaboradores do QL-Win (Copyright © 2017-2026 QL-Win Contributors)
- Licença: GNU General Public License, versão 3 ou posterior (GPL-3.0). Texto completo: https://github.com/QL-Win/QuickLook/blob/master/LICENSE e https://www.gnu.org/licenses/gpl-3.0.html
- Versão usada: a versão portátil oficial mais recente, conferida pelo SHA-256 que o GitHub publica para cada arquivo. Se o GitHub não responder, o MacKeys instala a 4.5.0:
  - `QuickLook-4.5.0.7z`, SHA-256 `d1d578deb75b83ea09255b89382b088d35d38125c0c2b309825b1e20ac962b91`
  - `QuickLook-4.5.0.zip` (alternativa), SHA-256 `852d8bcccd984e416fc8491ccedb848f0c3472e930ee0d292188b2ea3df524e0`
- Código-fonte dessa versão: https://github.com/QL-Win/QuickLook/tree/4.5.0

O MacKeys **não inclui nem modifica** o código ou os binários do QuickLook. Durante a instalação (ou pelo botão em Configurações › Quick Look), o MacKeys baixa o arquivo oficial acima dos lançamentos do projeto no GitHub, confere o SHA-256 e o extrai sem alterações em `%LOCALAPPDATA%\Programs\MacKeys\QuickLook`. Os dois programas rodam como processos separados: o MacKeys só abre e fecha o QuickLook e grava preferências no arquivo de configurações do próprio QuickLook: `ShowTrayIcon = False` (esconde o ícone dele na bandeja), `DisableAutoUpdateCheck = True` (a versão é atualizada pelo MacKeys) e as opções que você muda em Configurações › Quick Look.

### Animated Fluent Emojis

Os ícones animados do README vêm de https://github.com/Tarikul-Islam-Anik/Animated-Fluent-Emojis, baseados no Fluent Emoji da Microsoft (https://github.com/microsoft/fluentui-emoji, licença MIT). Eles aparecem só na página do repositório e não fazem parte do app.
