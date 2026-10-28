# Especificação de produto: sincronização incremental com Steam

**Estado:** extensão comunitária Playnite GameLibrary em desenvolvimento. O `main` público do fork implementa exportação inicial filtrada, fila incremental idempotente e sincronização assíncrona de arte. A validação passou em 318 testes e conferiu 48 atalhos e 240 arquivos de arte. O código está publicado no fork; não há pacote ou release redistribuível, e a licença upstream não está declarada.
**Alvo:** Windows, .NET Framework 4.6.2, Playnite 10.63 e SDK 6.18.0 (alinhado à instância ativa).

## 1. Objetivo

Manter Playnite como catálogo principal e disponibilizar no Steam os jogos externos escolhidos pelas fontes e filtros. A primeira exportação aplica os filtros à biblioteca elegível e escreve o lote de uma vez. Depois do baseline, eventos do banco Playnite alimentam uma fila assíncrona, persistente e idempotente.

## 2. Escopo funcional

- Antes do baseline, **Sync Playnite → Steam…** prepara a exportação inicial filtrada. Ela seleciona jogos de fontes escolhidas e respeita instalação, visibilidade e opções de ação de emulador/URL. Só jogos com ação suportada e resolvível entram no lote.
- O addon mostra uma confirmação com a quantidade de jogos e executa um único lote de exportação. O baseline só é marcado após ao menos um atalho ser gravado com sucesso. VDF vazio, erro, Steam ativo ou seleção sem jogo exportável não completam o baseline.
- A exportação manual após o baseline continua usando a lista de seleção existente, atualiza o mapeamento da fila e não redefine o baseline.
- A automação começa desligada. O usuário seleciona fontes e filtros, conclui o baseline e ativa **Automatically add eligible new games to Steam**.
- Ativar a automação ou mudar filtros/fontes após o baseline agenda uma reconciliação filtrada para cobrir eventos perdidos durante a transição. Uma sessão limpa do Playnite não inicia uma varredura completa ao abrir.
- Depois do baseline, `Games.ItemCollectionChanged.AddedItems` observa jogos adicionados das fontes elegíveis. `Games.ItemUpdated` atualiza apenas jogos já observados, enfileirados ou exportados, cobrindo metadados, ação e instalação que chegam depois.
- Jogos das fontes selecionadas são observados também quando um filtro temporário os exclui, para que uma atualização posterior possa torná-los elegíveis. Itens ocultos, o próprio addon e fontes Steam são excluídos.
- Arquivos e perfis de emulador suportados são exportados diretamente. Se um jogo de provedor não persistir `GameActions`, o addon consulta os plugins carregados pela API do Playnite. Quando há exatamente um `PlayController` dinâmico e nenhuma ação `Play` configurada, o atalho chama `Playnite.DesktopApp.exe --start <Game.Id> --actionindex 0 --nolibupdate`; o Playnite volta a usar o controlador oficial do provedor. O callback de evento apenas persiste o snapshot; essa consulta ocorre no worker, pela UI dispatcher. Se não houver um controlador único, o resolver tenta executável no diretório de instalação até profundidade dois e aceita apenas candidato único ou correspondência clara por nome. Ambiguidade, ausência de executável e vários controladores dinâmicos geram `NeedsAttention`, sem escolher launcher genérico ou abrir diálogo. O snapshot conserva ID externo e ROMs de `Game.Roms`, necessários para reconstruir variáveis como `{ImagePath}` após reinício. URLs exigem opção explícita.
- Nenhuma operação automática fecha ou reinicia Steam. A fila aguarda o cliente terminar. Remover jogo do Playnite nunca remove atalho Steam.
- Para cada atalho, o addon permite configuração desktop e overlay Steam (AllowDesktopConfig=1, AllowOverlay=1), mas não cria layout Steam Input por jogo. O mapeamento continua sob controle do Steam e do usuário.

## 3. Filtros e elegibilidade

1. Excluir itens ocultos, jogos do próprio addon e fontes cujo nome é `Steam` ou começa com `Steam `, incluindo Family Sharing.
2. Selecionar fontes por `Game.SourceId` do Playnite. Fontes diferentes do mesmo `PluginId` são independentes. Jogos sem `PluginId` só entram com ação `Emulator` e **Include supported emulator actions** ligado.
3. **Only installed games** exclui jogos não instalados; estes continuam observados quando pertencem a uma fonte selecionada.
4. **Include supported emulator actions** controla ações e perfis de emulador.
5. **Include URL launch actions** controla ações URL.
6. A exportação inicial aplica esses filtros antes de montar o lote. Exportações manuais posteriores atualizam os mapeamentos dos jogos exportados e não redefinem o baseline.

## 4. Idempotência e persistência

- Chave local do jogo: `Game.Id` (GUID); fila separada por SteamID64. Auto-detect prioriza a conta única `MostRecent` em `loginusers.vdf`; seleção ambígua exige escolher um perfil.
- Fingerprint cobre fonte, nome, instalação, ROMs, campos da ação e referências de Icon, CoverImage e BackgroundImage. ItemUpdated em jogos rastreados força verificação do conteúdo mesmo quando o caminho não muda.
- Journal JSONL recebe sequência monotônica e flush antes do worker processar.
- Manifesto atômico guarda baseline, sessão, AppID, fingerprint e estado da operação.
- Fingerprints repetidos não geram nova operação. Snapshots novos substituem os anteriores pendentes do mesmo GUID.
- Ao compactar o journal, preservar o último estado `FilteredOut` por jogo como tombstone de deduplicação; repetir o mesmo snapshot após compactação/reinício não deve alocar outra sequência.
- O AppID preparado é persistido antes do replace; replay reutiliza o mesmo AppID. Exportação em lote valida colisões de identidade antes de gravar.
- Reconciliação completa só ocorre ao ativar automação, mudar filtros/fontes, por pedido explícito ou recuperação de sessão interrompida.

Estados: `Pending`, `Prepared`, `WaitingForSteam`, `RetryableFailure`, `NeedsAttention`, `FilteredOut`, `Committed` e `Superseded`. `FilteredOut` não é reexecutado até uma atualização produzir snapshot diferente.

## 5. Escrita segura

1. Verificar se Steam está fechado e ler o VDF atual.
2. Mesclar por AppID, preservar campos desconhecidos, tags e atalhos alheios.
3. Criar backup gerenciado e verificar a cópia.
4. Escrever temporário no mesmo diretório, reler pelo parser e validar o conteúdo.
5. Confirmar novamente que Steam continua fechado e o VDF de origem não mudou.
6. Substituir atomicamente e registrar o resultado no manifesto.

Falha no backup bloqueia a escrita. O fingerprint é capturado mesmo quando o chamador não fornece um e é validado antes e depois do backup; se o alvo mudar durante a cópia, a escrita aborta sem substituir a alteração. O parser também rejeita VDF truncado, estrutura inesperada, chaves duplicadas ignorando caixa e bytes após o terminador; AppID duplicado ou identidade ambígua exige atenção. A API do Steam não oferece lock cooperativo; permanece uma pequena janela entre a última verificação do processo e o replace.

## 6. Interface

As configurações controlam automação, fontes, filtros, status, retry e reconciliação explícita. O primeiro export filtrado é iniciado pelo menu Playnite → Steam e usa confirmação de lote; não há reset de baseline nem retry individual na interface atual.

## 7. Fora do escopo

- Sincronizar com Playnite fechado.
- Encerrar Steam silenciosamente.
- Remover atalhos Steam automaticamente.
- Garantir compatibilidade com todas as ações customizadas, scripts, launchers e emuladores.
- Distribuir antes de confirmar a licença e autorização do upstream.

## 8. Validação

Na validação ao vivo em 2026-10-28, Playnite 10.63 exportou 48 atalhos elegíveis depois de confirmar o lote filtrado. O parser leu 48 atalhos únicos. As operações de arte conferiram 240 arquivos de destino com as fontes atuais por SHA-256, e os 48 campos Icon do VDF apontaram para os caminhos preparados.

O snapshot de antes da sincronização foi comparado com o VDF final: executáveis, diretórios de trabalho e argumentos de lançamento permaneceram iguais. As tags do Playnite foram atualizadas. A gravação do VDF usou backup validado e replace atômico após a Steam fechar normalmente; depois, a Steam foi reaberta.

Um LibraryPlugin temporário forneceu metadados ao importador real do Playnite. O evento AddedItems entrou na fila, o worker processou a operação assincronamente e um atalho temporário iniciou pelo Big Picture. A limpeza removeu o item de teste e confirmou que os atalhos prévios continuaram presentes.

A suíte atual tem 318 testes aprovados, 0 falhas e 0 ignorados. O build Release terminou com 0 avisos e 0 erros. O teste controlado não substitui um evento de importação originado por conector comercial.

Ainda faltam estabilidade prolongada, reboot do Windows, revisão visual prolongada no Big Picture e uma inicialização limpa dedicada para verificar a migração de arte. O fork contém o código no `main`, mas não tem release ou pacote `.pext` público; o upstream não declara licença em 2026-10-28.
