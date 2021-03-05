unit WorkerControl.WorkerGroup;

interface

uses
  System.Classes, Generics.Collections, WorkerControl.Worker, Windows,
  ZapMQ.Wrapper, ZapMQ.Message.JSON, JSON, SyncObjs,
  WorkerControl.BoostWorkerGroup;

type
  TWorkerGroup = class(TThread)
  private
    FEvent : TEvent;
    FTotalWorkers: integer;
    FApplicationFullPath: string;
    FWorkers: TObjectList<TWorker>;
    FZapMQWrapper : TZapMQWrapper;
    FName: string;
    FTimeoutKeepAlive: Cardinal;
    FMonitoringRate: Cardinal;
    FEnabled: boolean;
    FBoost: TBoostWorkerGroup;
    FConfigReloaded: boolean;
    procedure SetApplicationFullPath(const Value: string);
    procedure SetTotalWorkers(const Value: integer);
    procedure SetWorkers(const Value: TObjectList<TWorker>);
    function GetStartUpInfo : TStartUpInfo;
    procedure AssertWorkersConfig;
    procedure StartWorker;
    procedure SafeStopWorker; overload;
    procedure SafeStopWorker(const pWorker : TWorker); overload;
    function StopWorker(const pWorker : TWorker) : boolean; overload;
    procedure WorkerNotResponding(const pMessage: TZapJSONMessage);
    function GetWorkerByProcessID(const pProcessId : Cardinal) : TWorker;
    procedure SendKeepAliveWorkers;
    procedure KeepAliveHandlerRPC(pMessage: TJSONObject; var pProcessing: boolean);
    procedure SetName(const Value: string);
    procedure SetMonitoringRate(const Value: Cardinal);
    procedure SetTimeoutKeepAlive(const Value: Cardinal);
    procedure SetEnabled(const Value: boolean);
    procedure SetBoost(const Value: TBoostWorkerGroup);
    procedure SetConfigReloaded(const Value: boolean);
  protected
    procedure Execute; override;
  public
    property Enabled : boolean read FEnabled write SetEnabled;
    property Name : string read FName write SetName;
    property ApplicationFullPath : string read FApplicationFullPath write SetApplicationFullPath;
    property TotalWorkers : integer read FTotalWorkers write SetTotalWorkers;
    property MonitoringRate : Cardinal read FMonitoringRate write SetMonitoringRate;
    property TimeoutKeepAlive : Cardinal read FTimeoutKeepAlive write SetTimeoutKeepAlive;
    property Boost : TBoostWorkerGroup read FBoost write SetBoost;
    property Workers : TObjectList<TWorker> read FWorkers write SetWorkers;
    property ConfigReloaded : boolean read FConfigReloaded write SetConfigReloaded;
    procedure StartWorkers;
    procedure StopWorkers;
    constructor Create(const pZapMQHost : string; const pZapMQPort : integer); overload;
    destructor Destroy; override;
  end;

implementation

uses
  ShellApi, System.DateUtils, System.SysUtils, WorkerControl.KeepAliveMessage,
  WorkerControl.SafeStopMessage;

{ TWorkerGroup }

procedure TWorkerGroup.AssertWorkersConfig;
var
  Dif : integer;
  I: Integer;
begin
  if ConfigReloaded then
  begin
    ConfigReloaded := False;
    if (Enabled) and (Workers.Count = 0)  then
    begin
      for I := 0 to Pred(TotalWorkers) do
        StartWorker;
    end;

    if (not Enabled) and (Workers.Count > 0) then
    begin
      for I := Pred(Workers.Count) downto 0 do
        SafeStopWorker;
    end;

    if (Enabled) and (Boost.Enabled) then
    begin
      if Boost.EndTime > Time then
        if Boost.StartTime <= Time then
          TotalWorkers := TotalWorkers + Boost.BoostWorkers;
    end;
  end;

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
        SafeStopWorker;
    end;
  end;
end;

constructor TWorkerGroup.Create(const pZapMQHost : string; const pZapMQPort : integer);
begin
  inherited Create(True);
  FWorkers := TObjectList<TWorker>.Create(True);
  FBoost := TBoostWorkerGroup.Create;
  FZapMQWrapper := TZapMQWrapper.Create(pZapMQHost, pZapMQPort);
  FZapMQWrapper.OnRPCExpired := WorkerNotResponding;
  FEvent := TEvent.Create(nil, True, False, '');
end;

destructor TWorkerGroup.Destroy;
begin
  FZapMQWrapper.SafeStop;
  FZapMQWrapper.Free;
  FWorkers.Free;
  FBoost.Free;
  FEvent.Free;
  inherited;
end;

procedure TWorkerGroup.Execute;
begin
  inherited;
  while not Terminated do
  begin
    AssertWorkersConfig;
    SendKeepAliveWorkers;
    FEvent.WaitFor(FMonitoringRate);
  end;
end;

function TWorkerGroup.GetStartUpInfo: TStartUpInfo;
begin
  Result.cb := 2048;
  Result.lpReserved := nil;
  Result.lpDesktop := nil;
  Result.lpTitle := nil;
  Result.dwFlags := STARTF_USESHOWWINDOW;
  //Result.wShowWindow := SW_HIDE;
  Result.wShowWindow := SW_SHOWNORMAL;
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

procedure TWorkerGroup.KeepAliveHandlerRPC(pMessage: TJSONObject;
  var pProcessing: boolean);
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
    pProcessing := False;
  end;
end;

procedure TWorkerGroup.SafeStopWorker(const pWorker: TWorker);
var
  SafeStopMessage : TSafeStopMessage;
  JSONMessage : TJSONObject;
begin
  SafeStopMessage := TSafeStopMessage.Create;
  SafeStopMessage.Text := 'STOP';
  JSONMessage := SafeStopMessage.ToJSON;
  try
    FZapMQWrapper.SendMessage(pWorker.ProcessId.ToString + 'SS', JSONMessage, 10000);
    Workers.Remove(pWorker);
  finally
    JSONMessage.Free;
    SafeStopMessage.Free;
  end;
end;

procedure TWorkerGroup.SafeStopWorker;
var
  Worker : TWorker;
  SafeStopMessage : TSafeStopMessage;
  JSONMessage : TJSONObject;
begin
  Worker := Workers.First;
  SafeStopMessage := TSafeStopMessage.Create;
  SafeStopMessage.Text := 'STOP';
  JSONMessage := SafeStopMessage.ToJSON;
  try
    FZapMQWrapper.SendMessage(Worker.ProcessId.ToString + 'SS', JSONMessage, 10000);
    Workers.Remove(Worker);
  finally
    JSONMessage.Free;
    SafeStopMessage.Free;
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
    KeepAliveMessage.ProcessId := Worker.ProcessId.ToString;
    JSONMessage := KeepAliveMessage.ToJSON;
    try
      FZapMQWrapper.SendRPCMessage(Worker.ProcessId.ToString, JSONMessage,
        KeepAliveHandlerRPC, FTimeoutKeepAlive);
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

procedure TWorkerGroup.SetBoost(const Value: TBoostWorkerGroup);
begin
  FBoost := Value;
end;

procedure TWorkerGroup.SetConfigReloaded(const Value: boolean);
begin
  FConfigReloaded := Value;
end;

procedure TWorkerGroup.SetEnabled(const Value: boolean);
begin
  FEnabled := Value;
end;

procedure TWorkerGroup.SetMonitoringRate(const Value: Cardinal);
begin
  FMonitoringRate := Value;
end;

procedure TWorkerGroup.SetName(const Value: string);
begin
  FName := Value;
end;

procedure TWorkerGroup.SetTimeoutKeepAlive(const Value: Cardinal);
begin
  FTimeoutKeepAlive := Value;
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
    NORMAL_PRIORITY_CLASS, nil, nil, GetStartUpInfo, ProcessInfo) then
  begin
    Worker := TWorker.Create;
    Worker.ProcessId := ProcessInfo.dwProcessId;
    Worker.LastKeepAlive := IncMinute(now, 5);
    Workers.Add(Worker);
  end;
  CloseHandle(ProcessInfo.hProcess);
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
  I : integer;
begin
  Terminate;
  FEvent.SetEvent;
  while not Terminated do;
  for I := Pred(Workers.Count) downto 0 do
  begin
    SafeStopWorker(Workers[i]);
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
      if StopWorker(Worker) then
        Workers.Remove(Worker);
  finally
    KeepAliveMessage.Free;
  end;
end;

end.
