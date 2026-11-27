# MacKeys

Atalhos de Mac no Windows, sem precisar desaprender. A tecla **Alt da esquerda** (onde fica o ⌘ no Mac) vira **Command**. A tecla **Win** continua sendo do Windows e ganha o papel do **⌥ Option** só onde faz sentido.

O Alt da direita não muda, então o AltGr continua funcionando no ABNT2.

Desenvolvido por Adson Pereira de Souza. Versão atual: **0.4.1 (teste)**.

## Atalhos

| Mac | Vira no Windows |
|---|---|
| ⌘ + qualquer tecla (C, V, X, Z, S, T, W, F, A…) | Ctrl + tecla |
| ⌘Tab / ⌘⇧Tab | Alt+Tab (segure ⌘ para continuar escolhendo) |
| ⌘Q | Alt+F4 |
| ⌘M / ⌘H | Minimizar janela |
| ⌘Espaço | Paleta de Comandos do PowerToys (ou pesquisa do Windows) |
| ⌘⌃Espaço | Painel de emojis |
| ⌘⇧3 | Print Screen |
| ⌘⇧4 / ⌘⇧5 | Ferramenta de Captura (Win+Shift+S) |
| ⌘⌥Esc | Gerenciador de Tarefas |
| Win + setas | Continuam do Windows (encaixar janelas, mover entre monitores) |

Desligados por padrão, mas disponíveis em Configurações › Atalhos: ⌘+setas (início e fim da linha ou do documento) e ⌥+setas (pular palavras).

## Mouse

| Mac | Vira no Windows |
|---|---|
| ⌘ + clique / arrastar / rolar | Ctrl + clique / arrastar / rolar |
| Win (⌥) + clique / arrastar / rolar | Alt + clique / arrastar / rolar |

Exemplos: ⌘+clique abre um link em nova aba, Win+arrastar duplica objetos no Illustrator e Win+rolar dá zoom no Photoshop.

## Perfis por app

| Perfil | Apps | O que muda |
|---|---|---|
| **Padrão** | Todos os outros (Notion, WhatsApp, navegador…) | Atalhos acima |
| **Explorador** | Explorador de Arquivos | Atalhos do Finder: ⌘P painel de visualização, ⌘↑ pasta acima, ⌘↓ abrir, ⌘[ ⌘] voltar/avançar, ⌘⌫ Lixeira, ⌘I propriedades, ⌘1–4 modos de exibição |
| **Documento** | Word, PowerPoint | ⌘↑/↓ pula parágrafo |
| **Planilha** | Excel | ⌘+setas vão até a borda dos dados |
| **Criativo** | Photoshop, Illustrator, InDesign, Premiere, After Effects, Revit, AutoCAD, CapCut | ⌘ = Ctrl e Win+tecla = Alt+tecla (o ⌥ dos atalhos Mac, incluindo ⌘⌥). ⌘Espaço vira Ctrl+Espaço (zoom) |
| **Desativado** | Área de Trabalho Remota, máquinas virtuais | O MacKeys não mexe no teclado |

Os perfis se escolhem em Configurações › Apps.

## Quick Look

No Explorador de Arquivos, selecione um arquivo e aperte **Espaço**. **Espaço** ou **Esc** fecham, e as **setas** trocam de arquivo.

| Tipo | Como aparece |
|---|---|
| Imagens (JPG, PNG, GIF, WebP, HEIC*…) | Imagem na orientação certa |
| PDF e Illustrator (.ai com compatibilidade PDF) | Páginas renderizadas pelo leitor de PDF do Windows |
| Excel, PowerPoint e outros com visualizador do Windows | O visualizador nativo do Windows |
| Word | Convertido para PDF pelo próprio Word, em segundo plano |
| Vídeo e áudio | Player com play/pause |
| Texto e código | Fonte monoespaçada |
| Todo o resto (PSD, RVT, DWG, ZIP, pastas…) | Miniatura grande e informações do arquivo |

\* HEIC e WebP dependem das extensões de imagem da Microsoft Store.

Se o **Peek do PowerToys** estiver configurado para abrir com Espaço, o Quick Look do MacKeys vem desligado, para os dois não disputarem a tecla.

## Instalar (usuário)

Baixe o pacote, extraia o .zip e rode o **Instalar.cmd**. Não precisa de administrador nem de .NET. Depois rode o **Testar.cmd**: ele faz o autoteste do teclado e abre um relatório em que todas as linhas devem ser "OK".

## Desenvolvimento

Requer o **.NET 8 SDK** no Windows 10/11.

```
dotnet build MacKeys
```

| Script | Para quê |
|---|---|
| `install.ps1` | Instala a versão em desenvolvimento em `%LOCALAPPDATA%\Programs\MacKeys` e preserva o `settings.json` |
| `clean-install.cmd` | Reinstala do zero (apaga configurações) |
| `make-package.ps1` | Gera `dist\MacKeys-<versão>.zip` com executável único, autossuficiente e compilado antecipadamente (ReadyToRun) |
| `tools/make-icon.ps1` | Regenera o ícone |

Modos de diagnóstico do executável:

| Comando | O que faz |
|---|---|
| `MacKeys.exe --selftest relatorio.txt` | Autoteste do teclado e do mouse numa janela própria. Configurações só em memória |
| `MacKeys.exe --screenshots pasta` | Gera imagens de todas as telas, claro e escuro |
| `MacKeys.exe --preview-test arquivo saida.png` | Abre o Quick Look de um arquivo e captura a tela |

Configurações em `%APPDATA%\MacKeys\settings.json`, log em `%APPDATA%\MacKeys\debug.log`.

### Arquitetura do teclado (leia antes de mexer)

- Os ganchos de teclado e mouse rodam numa **linha de execução dedicada**, de prioridade alta (`HookHost`). Na linha da interface, qualquer momento ocupado fazia o Windows entregar teclas sem passar pelo MacKeys, e uma soltura do ⌘ perdida deixava o Ctrl virtual preso.
- Antes de instalar o gancho, todo o caminho do teclado é **pré-compilado** (`KeyMapper.WarmUp`). Se a primeira tecla estourar o prazo do Windows, o gancho é removido para sempre.
- Nada de disco no gancho: o log é uma fila gravada em segundo plano.
- Um **vigia** recebe o teclado físico por Raw Input, vindo de um **processo auxiliar** (`--raw-helper`). Raw Input registrado no próprio processo faz o Windows deixar de chamar o gancho. Se o vigia percebe que o ⌘ foi solto sem o gancho ver, ele destrava tudo.
- A tecla Win **não é interceptada**: o Windows 11 ignora Win+setas reenviados depois de interceptados.
