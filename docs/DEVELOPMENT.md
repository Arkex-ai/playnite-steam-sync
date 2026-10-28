# Desenvolvimento e distribuição

## Estrutura do projeto

- Addon Playnite: src/SteamShortcutsImporter/SteamShortcutsImporter.csproj.
- Alvo: .NET Framework 4.6.2, Playnite SDK 6.18 e Playnite 10.63 na validação ao vivo.
- Manifesto: src/SteamShortcutsImporter/extension.yaml, tipo GameLibrary.
- Testes automatizados: tests/ShortcutsTests, executados em .NET 6.
- Empacotamento local: tools/pack.ps1 -Configuration Release.
- Branch de trabalho: feat/playnite-steam-sync, baseada em upstream/develop.

## Build e testes

Execute da raiz do repositório:

- dotnet test tests/ShortcutsTests/ShortcutsTests.csproj
- dotnet build src/SteamShortcutsImporter/SteamShortcutsImporter.csproj --configuration Release
- tools/pack.ps1 -Configuration Release

A validação local em 2026-10-28 teve 318 testes aprovados, 0 falhas e 0 ignorados. O build Release do addon concluiu com 0 avisos e 0 erros. Os testes podem emitir NU1701/NU1702 porque o projeto .NET 6 referencia assemblies do SDK/Playnite direcionados ao .NET Framework 4.6.2. O empacotamento requer uma versão compatível do Playnite Toolbox.

## Estado da validação real

- O fluxo inicial filtrado gravou 48 atalhos únicos no shortcuts.vdf.
- Após a atualização de arte, todos os 240 alvos do grid conferiram por SHA-256 com as fontes atuais do Playnite. Os 48 caminhos de ícone do VDF apontaram para os arquivos preparados.
- Executáveis, diretórios de trabalho e argumentos de lançamento dos 48 atalhos foram comparados com o snapshot anterior; não houve diferenças nesses campos. As tags atuais do Playnite foram aplicadas.
- O VDF foi salvo após o fechamento normal do Steam, com backup verificado e replace atômico. Steam foi reaberta após a escrita.
- Um LibraryPlugin temporário alimentou o importador real do Playnite. O evento AddedItems chegou ao worker assíncrono, foi confirmado na Steam e o atalho de teste iniciou pelo Big Picture.
- A branch local passou pela recuperação da fila depois de encerramento/reabertura do Playnite.

Os testes restantes estão listados no relatório de validação: importar um jogo novo por conector comercial, observar estabilidade prolongada, repetir após reboot e revisar as imagens visualmente no Big Picture. Uma inicialização limpa específica para provar que o marcador de migração de arte não repete a passagem ainda precisa de teste dedicado.

## Princípios de implementação

- Manter o baseline explícito e aplicar filtros antes de exportar.
- Depois do baseline, reagir a eventos do Playnite em vez de varrer toda a biblioteca a cada abertura.
- Persistir a fila antes do processamento; conservar sequência monotônica, coalescência idempotente e recuperação.
- Processar operações em série e preservar as mais novas enquanto itens esperam Steam ou retry.
- Verificar hashes das imagens, criar backups antes de substituir e usar replace atômico.
- Não alterar shortcuts.vdf enquanto Steam está aberta ou o estado do processo é incerto.
- Não excluir atalhos Steam automaticamente nem escolher ações ambíguas.

## Fork, branches e upstream

O repositório público [Arkex-ai/playnite-steam-sync](https://github.com/Arkex-ai/playnite-steam-sync) é um fork GitHub de hikaps/non-steam-sync. A implementação incremental e a documentação estão publicadas no `main` do fork. O commit foi preparado a partir de `upstream/develop`; as instruções existentes do upstream apontam `develop` como base para novos recursos e destino de PRs.

## Licença e releases

Em 2026-10-28, a API e a árvore pública do upstream não listavam um arquivo de licença. O GitHub permite visualizar e fazer fork do repositório dentro do serviço; isso não concede uma licença geral para redistribuir o addon como pacote `.pext` ou release fora da plataforma. Antes de distribuir um pacote ou release, confirmar a licença com o titular dos direitos. A finalidade sem fins lucrativos não substitui essa confirmação.
