# Worker Control 2.0 — Especificação

Situação: aprovada em 2026-10-07. Etapas 1, 2 e 3 implementadas. A etapa 1 está em uso no ambiente de desenvolvimento desde 2026-10-07.
Última revisão: 2026-10-08.

Este documento especifica o serviço Worker Control 2.0, reescrito em .NET. Corresponde à fase 8 do [plano do ZapMQ 2.0](https://github.com/MurilloLazzaretti/ZapMQ/blob/main/docs/PLANO-2.0.md) e detalha a seção 11 dele. O painel web, onde o Worker Control ganha uma seção, é especificado junto com o ZapMQ 2.2.

A versão 1.x (Delphi) continua na branch `delphi-v1`.

---

## 1. Objetivo e limites

O Worker Control mantém no ar um número definido de processos de cada aplicação ("workers"), agrupados por aplicação, e repõe os que caem ou travam.

A 2.0 tem de:

- substituir o serviço 1.x **sem alteração nas aplicações**: mesmas filas, mesmas mensagens, mesmo `ConfigWorkers.json`;
- corrigir as fragilidades conhecidas da 1.x (seção 2);
- oferecer o que a seção 11 do plano lista, em etapas (seção 13).

Fica fora desta especificação a interface web. Aqui se define o que o serviço faz e o contrato pelo qual ele é administrado; a tela vem depois e usa esse contrato.

## 2. Como a 1.x funciona e o que muda

| 1.x | 2.0 |
|---|---|
| Lê `ConfigWorkers.json` a cada `RateLoadConfig` | Detecta a alteração do arquivo na hora; `RateLoadConfig` vira só uma rede de segurança |
| Uma thread por grupo; a cada `MonitoringRate` acerta a quantidade de workers e envia keep-alive | Igual no que é observável; a quantidade também é acertada na hora quando um processo sai |
| Queda de processo só é notada quando o keep-alive vence | O serviço observa o processo e o repõe quando ele sai |
| Inicia todos os workers de uma vez | Subida escalonada e carência para o primeiro keep-alive (seção 6) |
| Keep-alive sem resposta: encerra o processo à força | Igual; registra o evento |
| Safe stop: envia a mensagem e esquece o processo | Espera a saída; passado o prazo, encerra à força |
| Workers só em memória: reiniciar o serviço duplica instâncias | Ao subir, reconhece os workers que já estão rodando |
| Reinício do ZapMQ faz keep-alives vencerem e workers saudáveis serem mortos | Sem conexão com o ZapMQ, keep-alive não conta |
| Reinício em laço sem limite nem registro | Espera crescente após quedas seguidas; grupo sinalizado |
| Boost avaliado só na recarga do arquivo; não cruza a meia-noite | Avaliado continuamente; várias janelas |
| Nenhum registro do que aconteceu | Log em arquivo e histórico de eventos |

## 3. Compatibilidade

O que não muda:

- **Filas e mensagens** trocadas com os workers (seção 4).
- **`ConfigWorkers.json`**: mesmo nome, mesmo lugar (pasta do executável), mesmas chaves. As chaves novas são todas opcionais.
- **Fila `WorkerControlAdmin`** com os comandos `CurrentWorkers` e `ReloadConfig`, nos formatos atuais. O Management Studio 1.x continua funcionando contra o serviço 2.0, inclusive o trace, que na 1.x é uma conversa direta entre o Management Studio e o worker.
- **Conta e forma de execução dos workers**: processos sem janela, sob a conta do serviço.
- **Workers em Delphi ou com a DLL antiga** são supervisionados como hoje.

O que muda para quem opera: o serviço deixa de ter instalador próprio (seção 12).

## 4. Contrato com os workers

Inalterado em relação à 1.x.

| Fila | Sentido | Mensagem | Resposta |
|---|---|---|---|
| `<pid>` | serviço → worker, RPC | `{"ProcessId": "<pid>"}` | objeto com `ProcessId` |
| `<pid>SS` | serviço → worker | `{"Text": "STOP"}` | — |
| `<pid>TR` | Management Studio → worker, RPC | `{"message": "start trace", "port": N}` | — |

`<pid>` é o número do processo do worker, em texto.

O keep-alive é uma mensagem RPC com validade `TimeoutKeepAlive`. Resposta dentro do prazo: o worker está vivo. Prazo vencido: o worker é considerado travado.

## 5. Estados de um worker

```
iniciando ──► no ar ──► parando ──► encerrado
    │           │           │
    │           │           └──► (prazo vencido) encerrado à força
    │           ├──► travado (keep-alive vencido) ──► encerrado à força
    │           └──► caiu (o processo saiu sozinho)
    └──► falhou ao iniciar
```

- **iniciando**: o processo foi criado e ainda não respondeu a nenhum keep-alive. Dura no máximo a carência de subida (seção 6).
- **no ar**: respondeu ao menos um keep-alive.
- **parando**: recebeu safe stop e ainda não saiu.

Cada mudança de estado gera um evento (seção 10).

## 6. Subida

Dois problemas da 1.x aparecem quando muitos workers iniciam juntos: a máquina fica saturada e os processos demoram a ficar prontos; e o primeiro keep-alive vence antes disso, o serviço os mata e inicia outros, que encontram a máquina ainda mais ocupada.

- **Subida escalonada.** O serviço inicia no máximo `StartBatchSize` workers por vez, em todos os grupos somados, com `StartBatchIntervalMs` entre um lote e o seguinte. Padrão: 4 workers a cada 2 segundos.
- **Carência de subida.** Um worker em estado *iniciando* tem `StartupGraceMs` para responder ao primeiro keep-alive, independentemente do `TimeoutKeepAlive`. Padrão: 60 segundos. Keep-alive vencido dentro da carência é reenviado, não conta como travamento.

Um keep-alive que vence para um worker *no ar* não o encerra na hora: o veredito sai 5 segundos depois, tempo para o serviço perceber se foi o ZapMQ que falhou (seção 7.3).

Com isso o `TimeoutKeepAlive` volta a medir só o que deveria: quanto tempo um worker já no ar pode ficar sem responder.

## 7. Supervisão

### 7.1 Quantidade

A quantidade desejada de um grupo habilitado é `TotalWorkers`, mais os workers de boost quando uma janela está ativa (seção 8), mais os de escala pela fila quando configurada (seção 8). Grupo desabilitado: zero.

O serviço compara desejado e existente a cada `MonitoringRate` e sempre que um processo sai ou a configuração muda. Falta: inicia. Sobra: safe stop nos mais antigos.

### 7.2 Queda

O serviço mantém o processo aberto e é avisado quando ele sai. A reposição acontece na hora, respeitando a proteção contra laço.

### 7.3 Keep-alive

Enviado a cada `MonitoringRate` para cada worker *no ar* ou *iniciando*. Vencido para um worker *no ar*: o processo é encerrado à força e reposto.

**Sem o ZapMQ, keep-alive não conta.** O serviço mantém uma mensagem própria circulando pelo ZapMQ, de ida e volta, praticamente o tempo todo. Um keep-alive sem resposta só é atribuído ao worker se, desde que foi enviado, essa mensagem nunca deixou de voltar. Vale para o ZapMQ parado, reiniciado ou inalcançável, e em qualquer dos dois protocolos. A supervisão por queda de processo (7.2) continua funcionando sem o ZapMQ.

### 7.4 Safe stop

O serviço envia a mensagem, coloca o worker em *parando* e espera a saída do processo por `SafeStopTimeoutMs` (padrão: 30 segundos). Passado o prazo, encerra à força e registra.

Um worker *parando* não conta na quantidade existente: se o grupo precisa de reposição, ela já é iniciada.

### 7.5 Proteção contra reinício em laço

Um worker que cai, trava ou falha ao iniciar menos de `CrashWindowMs` depois de ter sido iniciado conta como queda rápida. A partir de `CrashLimit` quedas rápidas seguidas no grupo, a reposição passa a esperar antes de cada nova tentativa: 5 s, 10 s, 20 s… até 5 minutos. O grupo fica sinalizado como instável até um worker permanecer no ar por mais que `CrashWindowMs`.

Padrões: janela de 60 segundos, limite de 3 quedas.

### 7.6 Reconhecimento de workers existentes

O serviço grava em disco, a cada mudança, os workers de cada grupo: número do processo e instante de criação.

Ao subir, para cada registro ele confere se existe um processo com aquele número, criado naquele instante e com o executável do grupo. Se existe, o worker é adotado e passa a ser supervisionado; se não, o registro é descartado. O instante de criação evita adotar um processo qualquer que tenha recebido o mesmo número.

Isso cobre a queda do próprio serviço e a troca do executável dele com os workers no ar (seção 12.2).

## 8. Quantidade variável

### 8.1 Boost

A chave `Boost` da 1.x continua valendo como uma janela diária. A 2.0 aceita também `BoostWindows`, uma lista:

```json
"BoostWindows": [
  { "Workers": 3, "StartTime": "22:00:00", "EndTime": "02:00:00", "Days": ["mon","tue","wed","thu","fri"] }
]
```

- `EndTime` menor que `StartTime` significa que a janela cruza a meia-noite.
- `Days` omitido: todos os dias. O dia é o do início da janela.
- Janelas sobrepostas não se somam: vale a de maior `Workers`.

O boost é avaliado continuamente; entra e sai no horário, sem depender da recarga do arquivo.

### 8.2 Escala pela fila

```json
"QueueScaling": { "Queue": "Pedidos", "PendingPerWorker": 50, "MaxWorkers": 8, "CooldownMs": 120000 }
```

O serviço consulta no ZapMQ, a cada 5 segundos, quantas mensagens estão pendentes na fila e mantém um worker a mais para cada `PendingPerWorker` pendentes completos (49 pendentes com `PendingPerWorker` de 50 não trazem nenhum), até `MaxWorkers` no total do grupo. Subir é imediato. Enquanto o ZapMQ não consegue informar a contagem, a quantidade fica como está. Depois que a fila esvazia, os workers a mais só saem após `CooldownMs`, para não oscilar.

Depende do ZapMQ 2.x, que expõe a contagem; contra um servidor 1.x a chave é ignorada, com aviso no log.

### 8.3 Reciclagem programada

```json
"Recycle": { "Time": "03:00:00", "Days": ["sun"] }
```

No horário, os workers do grupo são substituídos um a um: inicia o novo, espera ficar *no ar*, faz safe stop do antigo. O grupo nunca fica abaixo da quantidade desejada.

## 9. Configuração

`ConfigWorkers.json`, na pasta do executável.

### 9.1 Chaves existentes

`ZapMQHost`, `ZapMQPort`, `RateLoadConfig` e, por grupo, `Enabled`, `Name`, `ApplicationFullPath`, `TotalWorkers`, `MonitoringRate`, `TimeoutKeepAlive`, `Boost`. Mesmo significado da 1.x.

### 9.2 Chaves novas

Todas opcionais. As gerais ficam na raiz e valem para todos os grupos; as mesmas chaves dentro de um grupo valem só para ele.

| Chave | Onde | Padrão | Descrição |
|---|---|---|---|
| `StartBatchSize` | raiz | 4 | Workers iniciados por lote |
| `StartBatchIntervalMs` | raiz | 2000 | Intervalo entre lotes |
| `StartupGraceMs` | raiz, grupo | 60000 | Carência para o primeiro keep-alive |
| `SafeStopTimeoutMs` | raiz, grupo | 30000 | Espera pela saída depois do safe stop |
| `CrashWindowMs` | raiz, grupo | 60000 | Tempo no ar abaixo do qual uma queda é "rápida" |
| `CrashLimit` | raiz, grupo | 3 | Quedas rápidas seguidas até começar a esperar |
| `Arguments` | grupo | vazio | Argumentos passados ao executável |
| `WorkingDirectory` | grupo | pasta do executável do worker | Pasta de trabalho do processo |
| `BoostWindows` | grupo | — | Seção 8.1 |
| `QueueScaling` | grupo | — | Seção 8.2 |
| `Recycle` | grupo | — | Seção 8.3 |

Na 1.x o worker herdava a pasta de trabalho do serviço. O padrão novo, a pasta do próprio executável, é o que uma aplicação espera; quem dependia do comportamento antigo informa `WorkingDirectory`.

### 9.3 Leitura e gravação

- O serviço observa o arquivo e aplica a alteração na hora. Um arquivo inválido é recusado inteiro: a configuração em uso continua valendo e o erro vai para o log e para o histórico.
- Quando a alteração vem pelo contrato de administração (seção 11), o serviço grava o arquivo de forma atômica e guarda a versão anterior ao lado (`ConfigWorkers.json.bak`).
- Edição manual do arquivo continua possível.

## 10. Registros

| O quê | Onde |
|---|---|
| Log de diagnóstico | Arquivo diário na pasta `logs`, ao lado do executável |
| Workers em supervisão (seção 7.6) | `state.json`, ao lado do executável |
| Histórico de eventos e medições de saúde | SQLite (`workercontrol.db`), ao lado do executável |

**Eventos:** serviço iniciado e parado, configuração aplicada ou recusada, worker iniciado, no ar, caiu (com código de saída), travado, falhou ao iniciar, safe stop pedido, encerrado à força, adotado, grupo instável e estabilizado, boost e escala entrando e saindo, ação manual (com quem pediu, quando houver login).

**Saúde por worker**, colhida a cada `MonitoringRate`: tempo no ar, CPU, memória, tempo de resposta do último keep-alive.

O histórico é limitado: 30 dias de eventos e 24 horas de medições, configuráveis.

## 11. Contrato de administração

Pela fila `WorkerControlAdmin`, por RPC, como hoje. Não há porta nova: quem alcança o ZapMQ administra o Worker Control, de qualquer máquina.

### 11.1 Comandos da 1.x

`{"Message": "CurrentWorkers"}` e `{"Message": "ReloadConfig"}`, com as respostas atuais.

### 11.2 Comandos da 2.0

Têm o campo `Command` em vez de `Message`, e `Version` com a versão do contrato (1).

| Comando | Função |
|---|---|
| `Status` | Serviço, grupos e workers: estado, quantidade desejada e por quê (base, boost, escala), saúde, sinalização de instável |
| `GetConfig` / `SetConfig` | Lê e grava a configuração inteira, validada |
| `SetGroupEnabled` | Habilita ou desabilita um grupo |
| `SetGroupWorkers` | Muda `TotalWorkers` de um grupo |
| `RestartWorker` | Substitui um worker: inicia o novo e, quando ele está no ar, faz o safe stop do antigo |
| `RestartGroup` | Substitui os workers do grupo um a um, como na reciclagem |
| `Events` | Histórico, com filtro por grupo, tipo e período |
| `Health` | Medições de um worker ou grupo |
| `DetachAndStop` | Para o serviço deixando os workers rodando (seção 12.2) |
| `StartTrace` / `StopTrace` | Repassa a uma fila o trace de um worker que só conhece o trace da 1.x (seção 11.3) |
| `Frontends`, `Traffic`, `TrafficRoutes`, `TrafficPages`, `TrafficUpstreams`, `TrafficErrors` | Os módulos da aplicação web publicada na máquina e o tráfego lido do log do proxy reverso, também especificados em Monitoramento do ambiente |
| `DatabaseObjects`, `DatabaseObject` | Os objetos dos bancos acompanhados e o detalhe de cada um, com o script que o cria |
| `Database`, `DatabaseHistory`, `DatabaseQueries` | A saúde da instância de banco de dados do ambiente, seu histórico e as consultas mais caras. Especificados em `docs/BANCO.md` do ZapMQ |
| `ListServices`, `StartService`, `StopService`, `RestartService` | Serviços do Windows acompanhados sem serem iniciados pelo Worker Control. Especificados em [Monitoramento do ambiente](https://github.com/MurilloLazzaretti/ZapMQ/blob/main/docs/AMBIENTE.md); a versão do contrato passa a 2 e `Status` ganha `Services` |

Resposta: `{"Ok": true, ...}` ou `{"Ok": false, "Error": {"Code": "...", "Message": "..."}}`.

### 11.3 Trace

O trace de um worker é acompanhado pelo painel do ZapMQ, de qualquer máquina. Há dois caminhos, e quem assiste não precisa saber qual está em uso.

**Worker com o wrapper .NET 2.0.** O painel pede o trace ao próprio worker, pela fila `<pid>TR` que ele já consome:

| Mensagem em `<pid>TR` | Efeito |
|---|---|
| `{"message": "start zapmq trace", "queue": "<fila>", "lease": 30}` | O worker passa a publicar em `<fila>` o que a aplicação escrever com `Trace()`. Responde `{"message": "on"}` |
| `{"message": "stop zapmq trace"}` | Para de publicar |
| `{"message": "start trace", "port": N}` | O trace da 1.x, por socket, como sempre foi |

O worker publica em lotes, a cada 250 ms: `{"ProcessId": "<pid>", "Dropped": n, "Lines": [{"Seq": n, "At": "<UTC>", "Text": "..."}]}`. `Dropped` é quantas linhas ele descartou antes do lote, por falta de espaço (guarda no máximo 5.000) ou de conexão. O pedido vale por `lease` segundos; quem assiste o repete, e sem repetição o trace se desliga sozinho. Com o trace desligado, `Trace()` não faz nada.

**Worker em Delphi ou com wrapper anterior.** Só conhece o trace por socket na própria máquina. O painel pede então ao Worker Control:

| Comando | Campos | Efeito |
|---|---|---|
| `StartTrace` | `ProcessId`, `Queue`, `LeaseSeconds` (padrão 30) | O serviço abre uma porta local, manda ao worker o `start trace` da 1.x e publica em `Queue`, no formato acima, o que chegar pela porta. Repetido, renova o prazo |
| `StopTrace` | `ProcessId` | Desliga |

Só vale para worker mantido pelo serviço (`not-found` para os demais); `trace-failed` quando o worker não se conecta à porta. O texto que chega pelo socket não tem separador: o que chega junto é mostrado junto. É lido como UTF-8 e, se não for, como ANSI, que é o que o wrapper Delphi envia.

O trace é descartável: as filas em que ele trafega (`zapmq.trace.<pid>`) não geram mensagens mortas no ZapMQ, e o que ninguém consome some em segundos.

A publicação das alterações de estado para o painel atualizar a tela sem consultar a cada instante fica para a etapa 4, junto com o painel. Publicada numa fila sem consumidor, cada alteração viraria uma mensagem morta no ZapMQ 2.1; a forma de entrega é definida com quem vai consumir.

## 12. Serviço

### 12.1 Instalação

Como o ZapMQ 2.x: um executável único que leva o runtime, registrado com `sc.exe`, com o passo a passo no `README.md`. Sem instalador.

- Serviço Windows, início automático, conta Local System.
- Depende do serviço do ZapMQ quando os dois estão na mesma máquina.
- Ao subir sem conseguir falar com o ZapMQ, inicia os workers assim mesmo e passa a enviar keep-alive quando a conexão existir.

### 12.2 Parada

**Parar o serviço para todos os workers**, com safe stop e espera, como na 1.x. É o comportamento em que a operação se apoia hoje para trocar arquivos das aplicações.

Para trocar o executável do próprio Worker Control sem derrubar as aplicações existe a parada sem workers: o comando de administração `DetachAndStop`, ou o arquivo `detach.flag` na pasta do executável antes de parar o serviço. Os workers continuam rodando e são adotados quando o serviço volta (seção 7.6).

### 12.3 Conexão com o ZapMQ

Pelo wrapper .NET 2.0. Três conexões para o serviço inteiro (envio, administração e a mensagem de verificação da seção 7.3), em vez de uma por grupo.

### 12.4 Nome do serviço

`WorkerControlService`, com o nome de exibição `WorkerControl`: os mesmos da 1.x, que o Management Studio 1.x procura para mostrar o estado e para parar e iniciar o serviço. Onde a 1.x já está instalada, a troca é apontar o serviço existente para o executável novo.

### 12.5 Processos filhos

Encerrar um worker à força encerra também os processos que ele iniciou. Na 1.x eles ficavam órfãos.

## 13. Etapas

| Etapa | Entrega | Como se valida |
|---|---|---|
| 1 | Serviço 2.0 com tudo das seções 4 a 7, 9, 10 (log e `state.json`) e 12, mais os comandos da 1.x | Instalado no ambiente de desenvolvimento no lugar do serviço Delphi, com as mesmas aplicações e o mesmo `ConfigWorkers.json`; Management Studio 1.x operando contra ele |
| 2 | Histórico e saúde em SQLite; contrato de administração 2.0; boost com várias janelas, escala pela fila, reciclagem, ações manuais | Testes do contrato; uso pelo painel na etapa 4 |
| 3 | Trace pelo ZapMQ: wrapper de worker .NET 2.0 (mesma API) e as operações correspondentes no ZapMQ | Aplicação existente com a DLL trocada, trace acompanhado de outra máquina |
| 4 | Painel web (ZapMQ 2.2) com as seções do ZapMQ e do Worker Control | Operação completa pelo navegador; Management Studio aposentado |

Nenhuma etapa vai a outro ambiente antes de as quatro estarem funcionando no de desenvolvimento.

## 14. Decisões tomadas

1. **Ordem das etapas.** Serviço primeiro, com o Management Studio 1.x servindo de tela até o painel existir (etapa 1), em vez de serviço e painel juntos.
2. **Parar o serviço continua parando os workers** (seção 12.2), com a parada sem workers como alternativa explícita.
3. **Subida escalonada e carência** (seção 6), com os padrões de 4 workers a cada 2 segundos e 60 segundos de carência.
4. **Pasta de trabalho do worker** passa a ser a do executável dele (seção 9.2).
5. **Administração pela fila do ZapMQ**, sem porta própria (seção 11).

## 15. Verificação

- Testes do núcleo com relógio e processos simulados: cada transição da seção 5, subida escalonada, carência, proteção contra laço, boost (inclusive cruzando a meia-noite), escala e reciclagem.
- Testes com processos de verdade: um worker de teste que responde, trava, cai ou ignora o safe stop conforme o argumento recebido.
- Reconhecimento de workers: serviço encerrado à força e iniciado de novo, sem duplicar instâncias.
- ZapMQ parado e reiniciado com workers no ar: nenhum é encerrado.
- Os comandos `CurrentWorkers` e `ReloadConfig` comparados com as respostas do serviço Delphi.
- Instalação no ambiente de desenvolvimento com as aplicações reais.
