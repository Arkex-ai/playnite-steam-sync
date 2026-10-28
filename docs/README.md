# Documentação do Playnite Steam Sync

Extensão comunitária do Playnite para sincronizar jogos elegíveis e arte com atalhos não-Steam da Steam. A documentação cobre filtros, fila assíncrona idempotente, atualizações de arte, proteção do shortcuts.vdf e execução pelo Big Picture.

## Estado do projeto

- O `main` público contém a implementação incremental, aprovada em 318 testes automatizados e no build Release.
- A validação local com Playnite 10.63 e Steam real conferiu 48 atalhos e 240 arquivos de arte por SHA-256.
- Ainda falta validar a importação de um jogo novo por conector comercial, estabilidade prolongada, reboot do Windows e inspeção visual prolongada no Big Picture.
- O fork público está ligado ao upstream e contém o código e a documentação atualizados.
- O upstream não declara uma licença em 2026-10-28; não há release ou pacote `.pext` redistribuível publicado.

## Documentos

- [Guia do usuário](USER_GUIDE.md): configuração inicial, filtros, baseline, sincronização automática e recuperação.
- [Especificação do produto](PRODUCT_SPEC.md): objetivos, filtros, estados da fila, critérios e limites.
- [Arquitetura](ARCHITECTURE.md): eventos do Playnite, snapshots, idempotência, retries, arte e escrita segura do VDF.
- [Decisão de arquitetura](ADR-001-PLAYNITE-EMBEDDED.md): extensão Playnite versus serviço externo.
- [Desenvolvimento](DEVELOPMENT.md): build, testes, estrutura e distribuição.
- [Roadmap](ROADMAP.md): validações restantes e portão de distribuição.
- [Relatório de validação](VALIDATION_REPORT.md): resultados agregados dos testes automatizados e da instância real.

## O que a automação faz

Uma exportação inicial aplica os filtros escolhidos e estabelece o baseline. Depois disso, eventos de adição e atualização do Playnite alimentam uma fila persistente. A fila evita trabalho duplicado, recupera operações após reinício e atualiza arte de jogos já rastreados. Uma abertura limpa do Playnite não varre a biblioteca inteira.

Steam Input por jogo e mapeamento de controles do emulador continuam sob controle do usuário.
