unit WorkerControl.WorkerGroup;

interface

uses
  System.Classes, Generics.Collections, WorkerControl.Worker, Windows,
  ZapMQ.Wrapper, ZapMQ.Message.JSON, JSON;

type
  TWorkerGroup = class(TThread)
  private
    FLastExecute : Cardinal;
    FTotalWorkers: integer;
    FApplicationFullPath: string;
    FWorkers: TObjectList<TWorker>;
    FZapMQWrapper : TZapMQWrapper;
    FName: string;
    procedure SetApplicationFullPath(const Value: string);
    procedure SetTotalWorkers(const Value: integer);
    procedure SetWorkers(const Value: TObjectList<TWorker>);
    function GetStartUpInfo : TStartUpInfo;
    procedure EqualizeTotalWorkers;
    procedure StartWorker;
    function StopWorker : boolean; overload;
    function StopWorker(const pWorker : TWorker) : boolean; overload;
    procedure WorkerNotResponding(const pMessage: TZapJSONMessage);
    function GetWorkerByProcessID(const pProcessId : Cardinal) : TWorker;
    procedure SendKeepAliveWorkers;
    procedure KeepAliveHandlerRPC(pMessage: TJSONObject);
    procedure SetName(const Value: string);
  protected
    procedure Execute; override;
  public
    property Name : string read FName write SetName;
    property Workers : TObjectList<TWorker> read FWorkers write SetWorkers;
    property ApplicationFullPath : string read FApplicationFullPath write SetApplicationFullPath;
    property TotalWorkers : integer read FTotalWorkers write SetTotalWorkers;
    procedure StartWorkers;
    procedure StopWorkers;
    constructor Create(const pZapMQHost : string; const pZapMQPort : integer); overload;
    destructor Destroy; override;
  end;

implementation

uses
  ShellApi, System.DateUtils, System.SysUtils, WorkerControl.KeepAliveMessage;

{ TWorkerGroup }

procedure TWorkerGroup.EqualizeTotalWorkers;
var
  Dif : integer;
  I: Integer;
begin
  if TotalWorkers <> Workers.Count then
  begin
    if TotalWorkers > Workers.Count then
    begin
      Dif := TotalWorkers - Workers.Count;
      for I := 0 to Pred(Dif) do
        StartWorker;
    end
    else
    begin
      Dif := Workers.Count - TotalWorkers;
      for I := 0 to Pred(Dif) do
        StopWorker;
    end;
  end;
end;

constructor TWorkerGroup.Create(const pZapMQHost : string; const pZapMQPort : integer);
begin
  inherited Create(True);
  Workers := TObjectList<TWorker>.Create(True);
  FZapMQWrapper := TZapMQWrapper.Create(pZapMQHost, pZapMQPort);
  FZapMQWrapper.OnRPCExpired := WorkerNotResponding;
end;

destructor TWorkerGroup.Destroy;
begin
  FZapMQWrapper.Free;
  Workers.Free;
  inherited;
end;

procedure TWorkerGroup.Execute;
begin
  inherited;
  while not Terminated do
  begin
    if (FLastExecute + 30000) < GetTickCount then
    begin
      FLastExecute := GetTickCount;
      EqualizeTotalWorkers;
      SendKeepAliveWorkers;
    end;
    Sleep(100);
  end;
end;

function TWorkerGroup.GetStartUpInfo: TStartUpInfo;
begin
  Result.cb := 2048;
  Result.lpReserved := nil;
  Result.lpDesktop := nil;
  Result.lpTitle := nil;
  Result.dwFlags := STARTF_USESHOWWINDOW;
  Result.wShowWindow := SW_HIDE;
  //Result.wShowWindow := SW_SHOWNORMAL;
  Result.cbReserved2 := 0;
  Result.lpReserved2 := nil;
end;

function TWorkerGroup.GetWorkerByProcessID(const pProcessId: Cardinal): TWorker;
var
  Worker: TWorker;
begin
  Result := nil;
  for Worker in Workers do
  begin
    if Worker.ProcessId = pProcessId then
    begin
      Result := Worker;
      Break;
    end;
  end;
end;

procedure TWorkerGroup.KeepAliveHandlerRPC(pMessage: TJSONObject);
var
  KeepAliveMessage : TKeepAliveMessage;
  Worker : TWorker;
begin
  KeepAliveMessage := TKeepAliveMessage.FromJSON(pMessage);
  try
    Worker := GetWorkerByProcessID(KeepAliveMessage.ProcessId.ToInteger);
    if Assigned(Worker) then
      Worker.LastKeepAlive := now;
  finally
    KeepAliveMessage.Free;
  end;
end;

procedure TWorkerGroup.SendKeepAliveWorkers;
var
  Worker: TWorker;
  KeepAliveMessage : TKeepAliveMessage;
  JSONMessage : TJSONObject;
begin
  for Worker in Workers do
  begin
    KeepAliveMessage := TKeepAliveMessage.Create;
    try
      KeepAliveMessage.ProcessId := Worker.ProcessId.ToString;
      JSONMessage := KeepAliveMessage.ToJSON;
      FZapMQWrapper.SendRPCMessage(Worker.ProcessId.ToString, JSONMessage,
        KeepAliveHandlerRPC, 15000);
    finally
      JSONMessage.Free;
      KeepAliveMessage.Free;
    end;
  end;
end;

procedure TWorkerGroup.SetApplicationFullPath(const Value: string);
begin
  FApplicationFullPath := Value;
end;

procedure TWorkerGroup.SetName(const Value: string);
begin
  FName := Value;
end;

procedure TWorkerGroup.SetTotalWorkers(const Value: integer);
begin
  FTotalWorkers := Value;
end;

procedure TWorkerGroup.SetWorkers(const Value: TObjectList<TWorker>);
begin
  FWorkers := Value;
end;

procedure TWorkerGroup.StartWorker;
var
  ProcessInfo : TProcessInformation;
  Worker : TWorker;
begin
  if CreateProcess(PChar(ApplicationFullPath), nil, nil, nil, False,
    CREATE_NEW_CONSOLE or NORMAL_PRIORITY_CLASS, nil, nil,
    GetStartUpInfo, ProcessInfo) then
  begin
    Worker := TWorker.Create;
    Worker.ProcessId := ProcessInfo.dwProcessId;
    Worker.LastKeepAlive := IncMinute(now, 5);
    Workers.Add(Worker);
  end;
end;

procedure TWorkerGroup.StartWorkers;
begin
  Start;
end;

function TWorkerGroup.StopWorker(const pWorker : TWorker) : boolean;
var
  HandleTerminate : THandle;
begin
  HandleTerminate := OpenProcess(PROCESS_TERMINATE, False, pWorker.ProcessId);
  Result := TerminateProcess(HandleTerminate, 0);
end;

procedure TWorkerGroup.StopWorkers;
var
  Worker : TWorker;
begin
  Terminate;
  while not Terminated do;
  for Worker in Workers do
  begin
    StopWorker(Worker)
  end;
  Workers.Clear;
end;

procedure TWorkerGroup.WorkerNotResponding(const pMessage: TZapJSONMessage);
var
  KeepAliveMessage : TKeepAliveMessage;
  Worker : TWorker;
begin
  KeepAliveMessage := TKeepAliveMessage.FromJSON(pMessage.Body);
  try
    Worker := GetWorkerByProcessID(KeepAliveMessage.ProcessId.ToInteger);
    if Assigned(Worker) then
      StopWorker(Worker);
  finally
    KeepAliveMessage.Free;
  end;
end;

function TWorkerGroup.StopWorker : boolean;
var
  Worker : TWorker;
  HandleTerminate : THandle;
begin
  Worker := Workers.First;
  HandleTerminate := OpenProcess(PROCESS_TERMINATE, False, Worker.ProcessId);
  Result := TerminateProcess(HandleTerminate, 0);
  if Result then
  begin
    Worker.Free;
  end;
end;

end.
