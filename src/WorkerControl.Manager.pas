unit WorkerControl.Manager;

interface

uses
  System.Classes, Generics.Collections, WorkerControl.WorkerGroup, SyncObjs,
  ZapMQ.Wrapper, ZapMQ.Message.JSON, JSON, IniFiles;

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
    procedure GravarArqLog(pTexto: String);
    function LoadIniTag(const pIniFile, pIniBloc, pIniTag : string) : string;
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
  GravarArqLog('CHEGOU');
  if pMessage.Body.GetValue<string>('Message') = 'CurrentWorkers' then
  begin
    GravarArqLog('CURRENT WORKERS');
    Result := TStatusMessage.ToJSON(WorkerGroups);
    GravarArqLog('Result : ' + TStatusMessage.ToJSON(WorkerGroups).ToJSON);
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
  GravarArqLog('DESCEU');
  inherited;
end;

procedure TManager.Execute;
begin
  inherited;
  GravarArqLog('SUBIU');
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

procedure TManager.GravarArqLog(pTexto: String);
var
  lArqLog: String;
  lPathModulo: String;
  lExiste: Boolean;
  lTextFile: TextFile;
  lTry: Integer;
  lPathLog : string;
begin
  lPathLog := LoadIniTag('OjiTec.ini', 'GLOBAL', 'PATHLOG');
  lPathModulo := lPathLog + 'WorkerControl';
  if not DirectoryExists(lPathModulo) then CreateDir(lPathModulo);
  lPathModulo := lPathModulo +'\';

  lArqLog := lPathModulo + FormatDateTime('dd-mm-yy', Now) + '.LOG';
  lExiste := FileExists( lArqlog ) ;

  Assignfile(lTextFile, lArqlog);
  try
    {$I-}
    if lExiste then
    begin
      lTry := 0;
      Append(lTextFile);
      while (IOResult <> 0) and (lTry < 5) do
      begin
        Sleep(1000);
        Append(lTextFile);
        lTry := lTry + 1;
      end;
    end
    else
    begin
      ReWrite(lTextFile);
    end;
    {$I+}
    if IOResult = 0 then
    begin
      try
        Writeln(lTextFile, FormatDateTime('dd-mm-yyyy hh:mm:ss', Now) );
        Writeln(lTextFile, pTexto );
        Writeln(lTextFile, '--------------------------------------------------------------------------------------------');
      finally
        Flush(lTextFile);
      end;
    end;
  finally
    CloseFile(lTextFile);
  end;
end;

procedure TManager.LoadConfig;
var
  Config : TConfig;
  WorkerGroupConfig : TWorkerGroupConfig;
  WorkerGroup : TWorkerGroup;
begin
  GravarArqLog('LOAD CONFIG');
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

function TManager.LoadIniTag(const pIniFile, pIniBloc, pIniTag: string): string;
var
  IniFile: TInifile;
  PathApp: String;
begin
  PathApp := ExtractFilePath(Application.ExeName);
  if FileExists(PathApp + pIniFile) then
  begin
    IniFile := TIniFile.Create(PathApp + pIniFile);
    try
      Result := IniFile.ReadString(pIniBloc, pIniTag, '');
    finally
      IniFile.Free;
    end;
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

