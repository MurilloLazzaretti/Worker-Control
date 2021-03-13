unit ManagementStudio.Worker;

interface

uses
  Vcl.StdCtrls, ZapMQ.Wrapper, JSON, System.Win.ScktComp;

type
  TWorker = class
  private
    FMemoDest : TMemo;
    FZapMQ : TZapMQWrapper;
    FServerSocket : TServerSocket;
    FProcessId: Cardinal;
    FLastKeepAlive: TDateTime;
    FGroupName: string;
    FApplicationName: string;
    procedure SetLastKeepAlive(const Value: TDateTime);
    procedure SetProcessId(const Value: Cardinal);
    procedure SetGroupName(const Value: string);
    procedure SetApplicationName(const Value: string);
    procedure ServerSocketOn;
    procedure ContactWorker;
    procedure ContactWorkerHandler(pMessage : TJSONObject; var pProcessing : boolean);
    procedure OnWorkerConnect(Sender: TObject; Socket: TCustomWinSocket);
    procedure OnTrace(Sender: TObject; Socket: TCustomWinSocket);
    procedure OnWorkerDisconnect(Sender: TObject; Socket: TCustomWinSocket);
  public
    property ApplicationName : string read FApplicationName write SetApplicationName;
    property GroupName : string read FGroupName write SetGroupName;
    property ProcessId : Cardinal read FProcessId write SetProcessId;
    property LastKeepAlive : TDateTime read FLastKeepAlive write SetLastKeepAlive;
    procedure StartTrace(const DestMemo : TMemo; const ZapMQ : TZapMQWrapper);
    procedure StopTrace;
    procedure WorkerNotResponding;
  end;

implementation

uses
  SysUtils, Classes, Vcl.Forms, IdSocketHandle;

{ TWorker }

procedure TWorker.ContactWorker;
var
  Json : TJSONObject;
begin
  Json := TJSONObject.Create;
  try
    Json.AddPair('message', 'start trace');
    Json.AddPair('port', FServerSocket.Port.ToString);
    Json.AddPair('ProcessId', ProcessId.ToString);
    FZapMQ.SendRPCMessage(ProcessId.ToString + 'TR', Json,
      ContactWorkerHandler, 15000);
  finally
    Json.Free;
  end;
end;

procedure TWorker.ContactWorkerHandler(pMessage: TJSONObject;
  var pProcessing: boolean);
begin
  if pMessage.P['Response.message'].Value = 'on' then
  begin
    FMemoDest.Lines.Add('*** Waiting for worker to connect ***');
  end
  else
  begin
    FMemoDest.Lines.Add('*** Client disconnect ***');
    if Assigned(FServerSocket) then
    begin
      FServerSocket.Active := False;
      FServerSocket.Free;
    end;
  end;
  pProcessing := False;
end;

procedure TWorker.OnTrace(Sender: TObject; Socket: TCustomWinSocket);
begin
  FMemoDest.Lines.Add(FormatDateTime('DD/MM/YYYY hh:mm:ss.zzz', now) +
    ':' + string(Socket.ReceiveText));
end;

procedure TWorker.OnWorkerConnect(Sender: TObject; Socket: TCustomWinSocket);
begin
  FMemoDest.Lines.Add('*** Worker Connected ***');
end;

procedure TWorker.OnWorkerDisconnect(Sender: TObject; Socket: TCustomWinSocket);
begin
  FMemoDest.Lines.Add('*** Worker Disconnected ***');
  //StopTrace;
end;

procedure TWorker.ServerSocketOn;
begin
  FMemoDest.Lines.Add('*** Matching TCP port ***');
  FServerSocket := TServerSocket.Create(nil);
  FServerSocket.OnClientConnect := OnWorkerConnect;
  FServerSocket.OnClientRead := OnTrace;
  FServerSocket.OnClientDisconnect := OnWorkerDisconnect;
  FServerSocket.Port := 5800;
  while not FServerSocket.Active do
  begin
    try
      FServerSocket.Open;
    except
      FServerSocket.Port := FServerSocket.Port + 1;
    end;
  end;
  FMemoDest.Lines.Add('*** Listening on : '+ FServerSocket.Port.ToString +' ***');
end;

procedure TWorker.SetApplicationName(const Value: string);
begin
  FApplicationName := Value;
end;

procedure TWorker.SetGroupName(const Value: string);
begin
  FGroupName := Value;
end;

procedure TWorker.SetLastKeepAlive(const Value: TDateTime);
begin
  FLastKeepAlive := Value;
end;

procedure TWorker.SetProcessId(const Value: Cardinal);
begin
  FProcessId := Value;
end;

procedure TWorker.StartTrace(const DestMemo: TMemo; const ZapMQ : TZapMQWrapper);
begin
  FMemoDest := DestMemo;
  FZapMQ := ZapMQ;
  FMemoDest.Lines.Add('*** Starting Trace ***');
  FMemoDest.Lines.Clear;
  ServerSocketOn;
  FMemoDest.Lines.Add('*** Contacting Worker ***');
  ContactWorker;
end;

procedure TWorker.StopTrace;
begin
  FServerSocket.Close;
  FServerSocket.Free;
end;

procedure TWorker.WorkerNotResponding;
begin
  FMemoDest.Lines.Add('*** Worker not responding ***');
  FMemoDest.Lines.Add('*** Stoping Trace ***');
end;

end.
