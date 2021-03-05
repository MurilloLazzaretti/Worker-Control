unit WorkerControl.Manager;

interface

uses
  System.Classes, Generics.Collections, WorkerControl.WorkerGroup, SyncObjs;

type
  TManager = class(TThread)
  private
    FEvent : TEvent;
    FWorkerGroups: TObjectList<TWorkerGroup>;
    procedure SetWorkerGroups(const Value: TObjectList<TWorkerGroup>);
    procedure LoadConfig;
    function GetWorkerGroup(const pName : string) : TWorkerGroup;
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
  Vcl.Forms, System.SysUtils, WorkerControl.Config, JSON;

{ TManager }

constructor TManager.Create;
begin
  inherited Create(True);
  WorkerGroups := TObjectList<TWorkerGroup>.Create(True);
  FEvent := TEvent.Create(nil, True, False, '');
end;

destructor TManager.Destroy;
begin
  FEvent.Free;
  inherited;
end;

procedure TManager.Execute;
begin
  inherited;
  while not Terminated do
  begin
    LoadConfig;
    FEvent.WaitFor(30000);
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
        WorkerGroup.Debug := WorkerGroupConfig.Debug;
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
  Terminate;
  FEvent.SetEvent;
  while not Terminated do;
  for I := Pred(WorkerGroups.Count) downto 0 do
  begin
    WorkerGroup := WorkerGroups[i];
    WorkerGroup.StopWorkers;
    WorkerGroups.Remove(WorkerGroup);
  end;
  WorkerGroups.Free;
end;

end.
