unit WorkerControl.Manager;

interface

uses
  System.Classes, Generics.Collections, WorkerControl.WorkerGroup, SyncObjs,
  ZapMQ.Wrapper, ZapMQ.Message.JSON, JSON;

type
  TManager = class(TThread)
  private
    FEvent : TEvent;
    FWorkerGroups: TObjectList<TWorkerGroup>;
    FRateLoadConfig : Cardinal;
    FZapMQHost : string;
    FZapMQPort : integer;
    FZapMQWrapper : TZapMQWrapper;
    procedure SetWorkerGroups(const Value: TObjectList<TWorkerGroup>);
    procedure LoadConfig;
    function GetWorkerGroup(const pName : string) : TWorkerGroup;
    function AdminHandler(pMessage : TZapJSONMessage;
      var pProcessing : boolean) : TJSONObject;
  protected
    procedure Execute; override;
  public
    property WorkerGroups : TObjectList<TWorkerGroup> read FWorkerGroups write SetWorkerGroups;
    procedure Stop;
    constructor Create; overload;
    destructor Destroy; override;
  end;

implementation

uses
  Vcl.Forms, System.SysUtils, WorkerControl.Config, WorkerControl.StatusMessage;

{ TManager }

function TManager.AdminHandler(pMessage: TZapJSONMessage;
  var pProcessing: boolean): TJSONObject;
var
  Json : TJSONObject;
begin
  Result := nil;
  if pMessage.Body.GetValue<string>('Message') = 'CurrentWorkers' then
  begin
    Result := TStatusMessage.ToJSON(WorkerGroups);
  end
  else if pMessage.Body.GetValue<string>('Message') = 'ReloadConfig' then
  begin
    LoadConfig;
    Json := TJSONObject.Create;
    Json.AddPair('Message', 'OK');
    Result := Json;
  end;
  pProcessing := False;
end;

constructor TManager.Create;
begin
  inherited Create(True);
  WorkerGroups := TObjectList<TWorkerGroup>.Create(True);
  FEvent := TEvent.Create(nil, True, False, '');
end;

destructor TManager.Destroy;
begin
  if Assigned(FZapMQWrapper) then
    FZapMQWrapper.Free;
  FEvent.Free;
  inherited;
end;

procedure TManager.Execute;
begin
  inherited;
  while not Terminated do
  begin
    LoadConfig;
    FEvent.WaitFor(FRateLoadConfig);
  end;
end;

function TManager.GetWorkerGroup(const pName: string): TWorkerGroup;
var
  WorkerGroup : TWorkerGroup;
begin
  Result := nil;
  for WorkerGroup in WorkerGroups do
  begin
    if WorkerGroup.Name = pName then
    begin
      Result := WorkerGroup;
      break;
    end;
  end;
end;

procedure TManager.LoadConfig;
var
  Config : TConfig;
  WorkerGroupConfig : TWorkerGroupConfig;
  WorkerGroup : TWorkerGroup;
begin
  Config := TConfig.FromFile('ConfigWorkers.json');
  try
    FZapMQHost := Config.ZapMQHost;
    FZapMQPort := Config.ZapMQPort;
    FRateLoadConfig := Config.RateLoadConfig;
    if not Assigned(FZapMQWrapper) then
    begin
      FZapMQWrapper := TZapMQWrapper.Create(FZapMQHost, FZapMQPort);
      FZapMQWrapper.Bind('WorkerControlAdmin', AdminHandler);
    end;
    for WorkerGroupConfig in Config.WorkerGroupsConfig do
    begin
      WorkerGroup := GetWorkerGroup(WorkerGroupConfig.Name);
      if Assigned(WorkerGroup) then
      begin
        WorkerGroup.ConfigReloaded := True;
        WorkerGroup.Enabled := WorkerGroupConfig.Enabled;
        WorkerGroup.TotalWorkers := WorkerGroupConfig.TotalWorkers;
        WorkerGroup.ApplicationFullPath := WorkerGroupConfig.ApplicationFullPath;
        WorkerGroup.MonitoringRate := WorkerGroupConfig.MonitoringRate;
        WorkerGroup.TimeoutKeepAlive := WorkerGroupConfig.TimeoutKeepAlive;
        WorkerGroup.Boost.Enabled := WorkerGroupConfig.Boost.Enabled;
        WorkerGroup.Boost.BoostWorkers := WorkerGroupConfig.Boost.BoostWorkers;
        WorkerGroup.Boost.StartTime := WorkerGroupConfig.Boost.StartTime;
        WorkerGroup.Boost.EndTime := WorkerGroupConfig.Boost.EndTime;
      end
      else
      begin
        WorkerGroup := TWorkerGroup.Create(Config.ZapMQHost, Config.ZapMQPort);
        WorkerGroup.ConfigReloaded := True;
        WorkerGroup.Enabled := WorkerGroupConfig.Enabled;
        WorkerGroup.Name := WorkerGroupConfig.Name;
        WorkerGroup.ApplicationFullPath := WorkerGroupConfig.ApplicationFullPath;
        WorkerGroup.TotalWorkers := WorkerGroupConfig.TotalWorkers;
        WorkerGroup.MonitoringRate := WorkerGroupConfig.MonitoringRate;
        WorkerGroup.TimeoutKeepAlive := WorkerGroupConfig.TimeoutKeepAlive;
        WorkerGroup.Boost.Enabled := WorkerGroupConfig.Boost.Enabled;
        WorkerGroup.Boost.BoostWorkers := WorkerGroupConfig.Boost.BoostWorkers;
        WorkerGroup.Boost.StartTime := WorkerGroupConfig.Boost.StartTime;
        WorkerGroup.Boost.EndTime := WorkerGroupConfig.Boost.EndTime;
        WorkerGroups.Add(WorkerGroup);
        WorkerGroup.StartWorkers;
      end;
    end;
  finally
    Config.Free;
  end;
end;

procedure TManager.SetWorkerGroups(const Value: TObjectList<TWorkerGroup>);
begin
  FWorkerGroups := Value;
end;

procedure TManager.Stop;
var
  WorkerGroup : TWorkerGroup;
  I : integer;
begin
  FZapMQWrapper.SafeStop;
  Terminate;
  FEvent.SetEvent;
  while not Terminated do;
  for I := Pred(WorkerGroups.Count) downto 0 do
  begin
    WorkerGroup := WorkerGroups[i];
    WorkerGroup.StopWorkers;
  end;
  WorkerGroups.Free;
end;

end.
