# Guia do usuário — Playnite Steam Sync

Esta extensão adiciona jogos elegíveis da biblioteca Playnite aos atalhos não-Steam da Steam e sincroniza a arte dos jogos rastreados. O fluxo automático só começa depois do baseline filtrado. A automação atual foi validada em Playnite 10.63 e Steam para Windows.

## Configuração inicial

Em **Add-ons → Extension Settings → Libraries → Non-Steam Sync**:

1. Escolha a pasta Steam e o perfil de destino.
   - **Auto-detect** usa o perfil único marcado como MostRecent em loginusers.vdf.
   - Se a Steam não indicar um perfil único e houver mais de um VDF possível, selecione a conta explicitamente.
2. Marque as fontes do Playnite que podem exportar jogos.
3. Defina os filtros:
   - **Only installed** inclui somente jogos instalados.
   - **Include emulators** permite ações de emulador suportadas.
   - **Include URLs** permite ações de URL; mantenha desligado se quiser apenas executáveis e emuladores.
4. Feche Steam normalmente e execute **Sync Playnite → Steam…** pela primeira vez.
5. Revise a contagem e confirme a exportação filtrada. O addon estabelece o baseline somente após uma gravação bem-sucedida.
6. Ative **Automatically add eligible new games to Steam**.

A primeira passagem percorre a biblioteca para aplicar os filtros. Depois do baseline, uma abertura limpa do Playnite não inicia nova varredura integral.

## Sincronização automática

Depois que a automação estiver ativa:

- Games.ItemCollectionChanged.AddedItems detecta jogos novos das fontes selecionadas.
- Games.ItemUpdated reavalia jogos observados, enfileirados ou já exportados. Isso cobre atualização tardia de metadados, ações de execução e arte.
- Uma fila persistente processa as mudanças em segundo plano, uma operação por vez. Snapshots idênticos são deduplicados e operações interrompidas podem ser recuperadas.
- Abrir o Playnite com sessão limpa não faz exportação em massa. Uma reconciliação filtrada acontece ao ativar a automação, alterar fontes/filtros, solicitar **Reconcile library now** ou recuperar uma sessão interrompida.

A arte pode ser copiada para o grid enquanto Steam está aberta. Se for preciso alterar shortcuts.vdf, o worker aguarda o fechamento normal do cliente e então grava o arquivo com backup verificado e replace atômico. A extensão não fecha Steam sozinha.

## Arte sincronizada

Para cada atalho rastreado, o addon verifica capa, poster, ícone, logo e fundo. Ele compara conteúdo por SHA-256; uma imagem idêntica não é regravada. Uma substituição passa por arquivo temporário, validação e backup. Remover uma imagem do Playnite não apaga automaticamente a cópia da Steam.

O Big Picture pode manter imagens em cache. Reiniciar Steam pode ser necessário para atualizar a interface após mudanças de ícone ou de shortcuts.vdf.

## Status da fila

O bloco **Automatic Playnite → Steam sync** resume baseline, pendências e erros.

- **Refresh status:** atualiza o resumo.
- **Retry queued items:** libera operações recuperáveis para nova tentativa.
- **Reconcile library now:** executa uma verificação filtrada explícita da biblioteca.

| Estado | Significado | Próximo passo |
|---|---|---|
| WaitingForSteam | Steam está ativa ou o estado do processo é incerto. | Feche Steam normalmente; a fila tenta novamente. |
| RetryableFailure | Falha temporária ao ler ou gravar arquivo/backup. | Verifique permissões e disponibilidade do diretório; use retry. |
| NeedsAttention | Ação, perfil, AppID ou VDF está ambíguo/inválido. | Corrija a configuração e tente novamente. |
| FilteredOut | Um filtro exclui o jogo no momento. | Ajuste os filtros ou atualize os dados do jogo. |
| Committed | Operação gravada e confirmada no estado persistente. | Reinicie Steam se o atalho ainda não aparecer. |

Jogos removidos do Playnite não são removidos automaticamente da Steam.

## Steam Big Picture e controles

Atalhos aceitos podem iniciar pela Steam Big Picture e usar o overlay. Steam Input e layouts de controle por jogo continuam sendo configurados pelo usuário. A extensão não configura controle de emulador.

## Problemas comuns

- **Jogo não entrou na fila:** confirme a fonte, instalação e filtros; jogos ocultos e fontes Steam são excluídos.
- **NeedsAttention:** confira se o jogo tem uma ação suportada e inequívoca no Playnite.
- **A fila espera Steam:** feche a Steam normalmente. O addon não encerra nem reinicia o cliente.
- **Arte antiga ainda aparece:** confirme o caminho no grid e reinicie Steam para limpar o cache visual.
- **Nada sincroniza enquanto Playnite está fechado:** o addon usa eventos do Playnite e não observa diretamente o banco privado enquanto o aplicativo está encerrado.
