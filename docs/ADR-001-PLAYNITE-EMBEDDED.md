# ADR-001: hospedar a sincronização dentro do Playnite

- **Status:** aceito para o escopo inicial.
- **Data:** 2026-10-27.

## Contexto

O objetivo é observar jogos recebidos pelo Playnite e exportá-los aos atalhos Steam. A extensão declara `Type: GameLibrary`; `Plugin` é a única classe concreta derivada de `LibraryPlugin` e delega a lógica de biblioteca/VDF para `ShortcutsLibrary`, um serviço interno. O assembly do plugin é varrido por tipos derivados de plugin pelo carregador do Playnite, então manter essa fronteira evita que a lógica interna seja carregada como uma segunda instância.

## Decisão

Implementar a fila no processo do Playnite, dentro da extensão existente. O handler captura dados e persiste o journal; um worker serial processa fora do callback. Não criar serviço Windows nem watcher do banco.

## Motivos

- `ItemCollectionChanged` entrega jogos adicionados sem comparação integral a cada abertura.
- `OnLibraryUpdated` e o estado de sessão permitem acionar recuperação depois que a biblioteca está carregada.
- A extensão já tem acesso ao SDK Playnite e aos componentes Steam.
- Evita acessar arquivos privados da base Playnite por fora do SDK.
- Evita duplicar instalação, configuração, logs e atualização de um segundo executável.

## Consequências

- A sincronização começa quando Playnite está aberto e já importou o jogo.
- Pendências persistidas retomam na próxima abertura.
- Trabalho contínuo com Playnite fechado fica fora do escopo.
- O worker reconstrói o snapshot persistido, incluindo `GameId` e a lista `Game.Roms`, para que `{ImagePath}` continue resolvido mesmo após reinício do Playnite. Ações que consultam o SDK Playnite, como resolver perfil de emulador, reconciliar jogos e salvar configurações, são encaminhadas pela UI dispatcher; o VDF e o backup continuam no worker serial.

## É um plugin oficial?

É uma **extensão comunitária do Playnite**, categoria `GameLibrary`. Ela não pertence ao núcleo nem é mantida oficialmente pelo time Playnite. Se listada no catálogo, aparece no browser integrado; isso não muda sua autoria para oficial. A documentação de segurança do Playnite diz que extensões comunitárias não são revisadas pela equipe central.

Em 2026-10-27, o README do `PlayniteAddonDatabase` informa que novas submissões estão suspensas por preocupações de segurança e carga de revisão; descreve um portal novo planejado até o fim de 2026 e possíveis exceções para membros confiáveis. Enquanto o processo não reabrir, o caminho funcional é publicar `.pext` em release própria e instalar manualmente. A inclusão no catálogo depende de aprovação quando o processo estiver disponível.

## Alternativas

### Serviço externo / Windows Service

Não escolhido. Para funcionar com Playnite fechado, teria de observar arquivos internos ou usar integração formal ainda não definida. Isso aumenta concorrência com o banco e risco de corrupção.

### Novo `GenericPlugin`

Não escolhido para a primeira versão. Também seria extensão dentro do Playnite, mas duplicaria recursos já presentes na extensão `GameLibrary`.

### Núcleo do Playnite

Fora do alcance do fork; requer decisão dos mantenedores do projeto principal.

## Portões de publicação

1. Resolver a licença de distribuição: em 2026-10-28, a branch upstream `main` não lista `LICENSE`, essa rota retorna 404 e o README observado não contém badge MIT. O fork foi publicado no GitHub usando o recurso de fork da plataforma; isso não confirma uma licença geral para redistribuir um pacote `.pext` ou release fora do serviço. Obter confirmação do titular antes dessa distribuição.
2. Conferir novamente o estado do portal de addons.
3. Validar Steam aberta/fechada, Big Picture, falhas, backup e recovery antes de distribuir.
