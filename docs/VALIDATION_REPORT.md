# Relatório de validação — Playnite Steam Sync

**Data da rodada:** 2026-10-28
**Build do addon:** manifesto 0.9.3; DLL local atualizada
**Ambiente:** Playnite 10.63 e Steam para Windows

## Resumo

O `main` público do fork combina exportação inicial filtrada e sincronização incremental por eventos do Playnite. A fila assíncrona e persistente é idempotente, serializa operações e recupera estado após reinício. A implementação de sincronização de arte foi carregada no Playnite ativo e conferida com uma instalação Steam real.

A documentação descreve resultados agregados. Não inclui SteamID, nomes de biblioteca, caminhos locais, IDs de atalhos ou hashes do VDF do perfil.

## Testes automatizados

- 318 aprovados, 0 falhas e 0 ignorados.
- O build Release do addon concluiu com 0 avisos e 0 erros.
- A suíte cobre filtros, fingerprints de arte, sequência e deduplicação, coalescência de snapshots, persistência do marcador de migração, recuperação, prioridade de operações pendentes, backups verificados, replace seguro e rejeição de VDF malformado.
- NU1701/NU1702 podem surgir nos testes .NET 6 por referências ao SDK/Playnite .NET Framework.

## Validação com Playnite e Steam reais

| Cenário | Resultado |
|---|---|
| Exportação inicial filtrada | O Playnite confirmou e gravou 48 atalhos elegíveis; 47 foram criados e 1 existente foi atualizado. |
| Evento controlado de jogo novo | Um LibraryPlugin temporário forneceu metadados ao importador real do Playnite. AddedItems enfileirou o snapshot e o worker confirmou a operação de forma assíncrona. |
| Lançamento pelo Big Picture | Um atalho temporário de teste iniciou pelo Steam Big Picture; o atalho temporário foi removido após o ensaio. |
| Integridade do VDF | O parser leu 48 atalhos únicos. A escrita protegida usou backup verificado, validação do fingerprint e replace atômico. |
| Sincronização de arte | 240 alvos de grid (48 jogos × 5 tipos) conferiram por SHA-256 com os arquivos fonte atuais do Playnite. |
| Referências do VDF | Os 48 campos Icon apontaram para os caminhos de arte preparados. |
| Campos de execução | Executáveis, diretórios de trabalho e argumentos dos 48 atalhos não mudaram nesta atualização de arte. As tags atuais do Playnite foram aplicadas. |
| Recuperação | A fila persistente recuperou após encerramento e reabertura do Playnite; a sequência do baseline foi preservada. |
| Steam ativa | Arquivos de arte puderam ser atualizados com o cliente aberto. A escrita do VDF aguardou o fechamento normal do Steam; Steam foi reaberta depois. |

## Limites ainda não validados

1. Uma importação de jogo novo originada por conector comercial; o ensaio usou um LibraryPlugin controlado junto ao pipeline real do Playnite.
2. Estabilidade assíncrona durante período prolongado e recuperação após reinicialização do Windows.
3. Inspeção visual prolongada de todas as imagens no Big Picture, incluindo efeitos do cache da Steam.
4. Inicialização limpa dedicada que comprove que a migração versionada de arte não repete a reconciliação.
5. Matriz maior de perfis Steam, combinações de fontes/filtros, ações URL, emuladores, VDF inválido e interrupção durante replace.

## Distribuição

O fork público contém a implementação e toda a documentação no `main`, além de manter a relação com o upstream. O upstream não declara uma licença em 2026-10-28. Não há release ou pacote `.pext` público; confirmar os direitos com o titular antes de distribuir um pacote ou release fora do GitHub.
