# Roteiro de testes: instalador e atualização (Velopack)

Para rodar quando o repositório **https://github.com/derpbizinoto/helmet-desk** estiver no ar (público).

Onde ficam as coisas:

| O quê | Pasta |
|---|---|
| App instalado (só para o usuário) | `%LocalAppData%\HelmetDesk\current\` |
| App instalado (todos os usuários, MSI) | `C:\Program Files\HelmetDesk\` |
| Dados (tarefas, notas, finanças, anexos) | `%AppData%\HelmetDesk\` |
| Logs, cópias de segurança, relatórios | `%LocalAppData%\HelmetDesk.Local\` |

> **Importante:** rode o Setup.exe / MSI sempre pelo Explorador de Arquivos (ou um terminal aberto pelo Menu Iniciar),
> nunca de dentro do app Claude. Programas iniciados por ele rodam isolados (MSIX): a instalação vai para
> `%LocalAppData%\Packages\Claude_…\LocalCache` e os atalhos e ícones da bandeja/Menu Iniciar ficam em branco.

As linhas do log que interessam começam com `atualização:` (Configurações → Suporte → Abrir pasta de logs).

## 0. Preparação (uma vez)

1. Criar o repositório público `helmet-desk` no GitHub e enviar o código:
   ```
   gh repo create helmet-desk --public --source . --remote origin
   git push -u origin main develop
   ```
2. Anotar quantos itens, lançamentos e etiquetas existem hoje (para comparar depois).

## 1. Atualização normal (instalação só para o usuário)

1. `.\build-release.ps1 1.0.0 -Upload` → confira a release **v1.0.0** em *Releases* no GitHub.
2. Instale pelo `HelmetDesk-win-Setup.exe` (se já estiver instalado, desinstale antes em Configurações do Windows → Aplicativos).
3. Abra o app: Configurações → Atualizações mostra **Versão atual: 1.0.0**. Clique em *Verificar atualizações* → "Você está usando a versão mais recente."
4. Faça uma mudança pequena e visível no código (ex.: um texto) e rode `.\build-release.ps1 1.0.1 -Upload`.
   - Confira em `.\releases` que saiu um `HelmetDesk-1.0.1-delta.nupkg` (prova de que a 1.0.0 foi baixada para o delta).
5. Abra o app 1.0.0 e espere ~10 s:
   - [ ] aparece o aviso "Nova versão 1.0.1 pronta" com **Reiniciar e atualizar**;
   - [ ] clicar nele fecha, atualiza e reabre o app;
   - [ ] Configurações → Atualizações mostra **1.0.1**;
   - [ ] tarefas, notas, finanças e anexos estão iguais.
6. Caminho "ignorar": publique **1.0.2**, deixe o aviso sumir, saia pela bandeja (*Sair*), abra de novo:
   - [ ] o app abre já na **1.0.2**.
7. Sem internet (desligue o Wi-Fi) e abra o app:
   - [ ] nada aparece para o usuário; o log tem `atualização: falha ao verificar ou baixar`.

## 2. MSI em modo "todos os usuários" (UAC — comportamento não documentado)

1. Desinstale a versão do teste 1.
2. Rode `HelmetDesk-win.msi` e escolha instalar **para todos os usuários** (deve pedir UAC na instalação; vai para `C:\Program Files\HelmetDesk`).
3. Abra o app (como usuário comum, não como administrador) e publique uma versão nova (`.\build-release.ps1 1.0.3 -Upload`).
4. Anote o resultado de cada passo:
   - [ ] o app detecta e **baixa** a versão nova? (o download vai para dentro da pasta de instalação)
   - [ ] se o download falhar por permissão: o log mostra o erro e o app continua funcionando?
   - [ ] **Reiniciar e atualizar**: aparece pedido de UAC? Se aceitar, atualiza e reabre?
   - [ ] se **recusar** o UAC: o app reabre na versão antiga, sem travar?
   - [ ] caminho "ignorar" (sair pela bandeja): aparece UAC ao fechar? A versão nova está lá na próxima abertura?
   - [ ] depois de tudo, os dados em `%AppData%\HelmetDesk` continuam intactos?

### Resultados (16/11/2026, Windows 11, MSI "todos os usuários", 1.0.1 → 1.0.2)

- Instalação pelo MSI pede UAC. Recusado: o assistente termina com "instalação interrompida", sem alterar nada.
- O app (rodando como usuário comum) detectou e baixou a 1.0.2 sem pedir nada.
- **Reiniciar e atualizar pede UAC.** Recusado: o app reabre na 1.0.1, sem erro e sem perder dados.
- Com a atualização baixada e recusada, **sair pela bandeja pede UAC de novo** (aceito → atualizou para 1.0.2).
- Não testado: UAC ao abrir o app com atualização pendente (o Velopack aplica na inicialização por padrão).

## 3. Desinstalar mantém os dados

1. Configurações do Windows → Aplicativos → Helmet Desk → Desinstalar.
2. Confira:
   - [ ] `%AppData%\HelmetDesk\` continua lá, com `data.json` e `attachments`;
   - [ ] `%LocalAppData%\HelmetDesk.Local\` continua lá (logs e cópias de segurança);
   - [ ] `%LocalAppData%\HelmetDesk\` (pasta do programa) foi removida;
   - [ ] atalhos da área de trabalho e do Menu Iniciar sumiram;
   - [ ] se "Iniciar com o Windows" estava ligado, a entrada sumiu de *Aplicativos de inicialização*.
3. Instale de novo: os dados aparecem como estavam.

## 4. Instalação limpa em outra máquina (sem dados pessoais)

Em outro PC, numa conta nova do Windows ou no **Windows Sandbox**:

- [ ] o SmartScreen avisa (instalador sem assinatura): *Mais informações → Executar assim mesmo*;
- [ ] instala sem pedir o .NET;
- [ ] o app abre só com a nota "Bem-vindo ao Helmet Desk", a tarefa "Planejar a semana", as etiquetas e categorias padrão — **nenhuma tarefa, nota ou finança sua**.

## Observação: o "Reparar" na primeira instalação

Quem usou o Helmet Desk antes do instalador existir tem a pasta antiga `%LocalAppData%\HelmetDesk` (logs/backups). O Setup a confunde com uma instalação e mostra "O Helmet Desk já está instalado" → **Reparar**, que apaga o conteúdo dela. Por isso o app agora usa `HelmetDesk.Local`. Para usuários novos isso não acontece.
