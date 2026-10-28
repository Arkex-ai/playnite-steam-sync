# Roadmap — Playnite Steam Sync

## Implementado e validado

- Extensão Playnite GameLibrary com exportação inicial filtrada para atalhos Steam.
- Baseline persistente seguido por sincronização incremental orientada a eventos.
- Fila assíncrona com sequência monotônica, deduplicação, coalescência, retry e recuperação.
- Atualização de arte para jogos rastreados, incluindo comparação de conteúdo por SHA-256 mesmo quando o caminho permanece igual.
- Backup verificado, validação de fingerprint concorrente e replace atômico para VDF e arquivos de arte.
- Perfil Steam selecionado de forma conservadora; operações ambíguas param em NeedsAttention.
- 318 testes automatizados aprovados e build Release sem avisos ou erros.
- Ensaio ao vivo conferiu 48 atalhos e 240 alvos de arte em uma biblioteca Playnite real.
- Um LibraryPlugin controlado alimentou o importador real do Playnite e exercitou AddedItems e o worker de produção.

## Validações antes de release

1. Repetir a importação com um conector comercial quando ele acrescentar um jogo novo.
2. Observar estabilidade e retries durante um período prolongado.
3. Repetir após reinicialização do Windows.
4. Fazer inspeção visual prolongada da arte no Big Picture e considerar o cache da Steam.
5. Cobrir mais perfis Steam, filtros, emuladores, URLs, VDFs inválidos e interrupções durante replace.
6. Fazer um ensaio limpo dedicado para confirmar que a migração versionada da arte não se repete depois de concluída.

## Fork e colaboração

O fork público [Arkex-ai/playnite-steam-sync](https://github.com/Arkex-ai/playnite-steam-sync) está ligado ao upstream hikaps/non-steam-sync. O `main` do fork contém as mudanças incrementais e a documentação. O commit foi preparado a partir de `develop`; o upstream recomenda abrir PRs para essa branch.

## Distribuição

O upstream não declara uma licença em 2026-10-28. Não publicar um pacote .pext ou release redistribuível até confirmar a licença com o titular dos direitos. A finalidade comunitária e sem fins lucrativos não substitui essa confirmação.
