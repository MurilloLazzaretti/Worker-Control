unit WorkerControl.Manager;

interface

uses
  System.Classes, Generics.Collections, WorkerControl.WorkerGroup;

type
  TManager = class(TThread)
  private
    FLastExecute : Cardinal;
    FWorkerGroups: TObjectList<TWorkerGroup>;
    FStoped : boolean;
    procedure SetWorkerGroups(const Value: TObjectList<TWorkerGroup>);
    function LoadTextConfig : string;
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
end;

destructor TManager.Destroy;
begin
  WorkerGroups.Free;
  inherited;
end;

procedure TManager.Execute;
begin
  inherited;
  FStoped := False;
  while not Terminated do
  begin
    if (FLastExecute + 60000) < GetTickCount then
    begin
      FLastExecute := GetTickCount;
      LoadConfig;
    end;
    Sleep(100);
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
  ConfigText : string;
  ConfigJSON : TJSONObject;
  Config : TConfig;
  WorkerGroupConfig, Wgc : TWorkerGroupConfig;
  WorkerGroup : TWorkerGroup;
begin
//  ConfigText := LoadTextConfig;
//  ConfigJSON := TJSONObject.ParseJSONValue(
//    TEncoding.ASCII.GetBytes(ConfigText), 0) as TJSONObject;
//  Config := TConfig.FromJSON(ConfigJSON);
  Config := TConfig.Create;
  wgc := TWorkerGroupConfig.Create;
  wgc.Name := 'Teste';
  wgc.ApplicationFullPath := 'C:\Temp\bossteste\Win32\Debug\WorkerWrapper.exe';
  wgc.TotalWorkers := 250;
  Config.WorkerGroupsConfig.Add(wgc);
  for WorkerGroupConfig in Config.WorkerGroupsConfig do
  begin
    WorkerGroup := GetWorkerGroup(WorkerGroupConfig.Name);
    if Assigned(WorkerGroup) then
    begin
      WorkerGroup.TotalWorkers := WorkerGroupConfig.TotalWorkers;
    end
    else
    begin
      WorkerGroup := TWorkerGroup.Create('localhost', 5679);
      WorkerGroup.Name := WorkerGroupConfig.Name;
      WorkerGroup.ApplicationFullPath := WorkerGroupConfig.ApplicationFullPath;
      WorkerGroup.TotalWorkers := WorkerGroupConfig.TotalWorkers;
      WorkerGroups.Add(WorkerGroup);
      WorkerGroup.StartWorkers;
    end;
  end;
  Config.Free;
end;

function TManager.LoadTextConfig: string;
var
  Config : TStringList;
begin
  Config := TStringList.Create;
  try
    Config.LoadFromFile(ExtractFilePath(Application.ExeName) + 'Config.js');
    Result := Config.Text;
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
begin
  Terminate;
  while not Terminated do;
  for WorkerGroup in WorkerGroups do
  begin
    WorkerGroup.StopWorkers;
    WorkerGroups.Remove(WorkerGroup);
  end;
end;

end.
