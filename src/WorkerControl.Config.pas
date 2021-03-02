unit WorkerControl.Config;

interface

uses
  Generics.Collections, JSON;

type
  TWorkerGroupConfig = class
  private
    FTotalWorkers: integer;
    FName: string;
    FApplicationFullPath: string;
    FTimeoutKeepAlive: Cardinal;
    FMonitoringRate: Cardinal;
    procedure SetApplicationFullPath(const Value: string);
    procedure SetName(const Value: string);
    procedure SetTotalWorkers(const Value: integer);
    procedure SetMonitoringRate(const Value: Cardinal);
    procedure SetTimeoutKeepAlive(const Value: Cardinal);
  public
    property Name : string read FName write SetName;
    property ApplicationFullPath : string read FApplicationFullPath write SetApplicationFullPath;
    property TotalWorkers : integer read FTotalWorkers write SetTotalWorkers;
    property MonitoringRate : Cardinal read FMonitoringRate write SetMonitoringRate;
    property TimeoutKeepAlive : Cardinal read FTimeoutKeepAlive write SetTimeoutKeepAlive;
  end;

  TConfig = class
  private
    FWorkerGroupsConfig: TObjectList<TWorkerGroupConfig>;
    FZapMQPort: integer;
    FZapMQHost: string;
    procedure SetWorkerGroupsConfig(
      const Value: TObjectList<TWorkerGroupConfig>);
    procedure SetZapMQHost(const Value: string);
    procedure SetZapMQPort(const Value: integer);
  public
    property ZapMQHost : string read FZapMQHost write SetZapMQHost;
    property ZapMQPort : integer read FZapMQPort write SetZapMQPort;
    property WorkerGroupsConfig : TObjectList<TWorkerGroupConfig> read FWorkerGroupsConfig write SetWorkerGroupsConfig;
    constructor Create; overload;
    destructor Destroy; override;
    class function FromFile(const pFileName : string) : TConfig;
  end;

implementation

uses
  Vcl.Forms, System.SysUtils, System.IOUtils;

{ TConfig }

constructor TConfig.Create;
begin
  WorkerGroupsConfig := TObjectList<TWorkerGroupConfig>.Create(True);
end;

destructor TConfig.Destroy;
begin
  WorkerGroupsConfig.Free;
  inherited;
end;

class function TConfig.FromFile(const pFileName : string) : TConfig;
var
  JSONArray : TJSONArray;
  JSONObject : TJSONObject;
  i: Integer;
  WorkerGroupConfig : TWorkerGroupConfig;
  fileName: TFileName;
begin
  Result := TConfig.Create;
  fileName := ExtractFilePath(Application.ExeName) + pFileName;
  JSONObject := TJSONObject.ParseJSONValue(TFile.ReadAllText(fileName)) as TJSONObject;
  try
    Result.ZapMQHost := JSONObject.GetValue<string>('ZapMQHost');
    Result.ZapMQPort := JSONObject.GetValue<integer>('ZapMQPort');
    JSONArray := JSONObject.GetValue<TJSONArray>('WorkerGroups');
    for i := 0 to Pred(JSONArray.Count) do
    begin
      WorkerGroupConfig := TWorkerGroupConfig.Create;
      WorkerGroupConfig.Name := JSONArray.Items[i].GetValue<string>('Name');
      WorkerGroupConfig.ApplicationFullPath := JSONArray.Items[i].GetValue<string>('ApplicationFullPath');
      WorkerGroupConfig.TotalWorkers := JSONArray.Items[i].GetValue<integer>('TotalWorkers');
      WorkerGroupConfig.MonitoringRate := JSONArray.Items[i].GetValue<Cardinal>('MonitoringRate');
      WorkerGroupConfig.TimeoutKeepAlive := JSONArray.Items[i].GetValue<Cardinal>('TimeoutKeepAlive');
      Result.WorkerGroupsConfig.Add(WorkerGroupConfig);
    end;
  finally
    JSONObject.Free;
  end;
end;

procedure TConfig.SetWorkerGroupsConfig(
  const Value: TObjectList<TWorkerGroupConfig>);
begin
  FWorkerGroupsConfig := Value;
end;

procedure TConfig.SetZapMQHost(const Value: string);
begin
  FZapMQHost := Value;
end;

procedure TConfig.SetZapMQPort(const Value: integer);
begin
  FZapMQPort := Value;
end;

{ TWorkerGroupConfig }

procedure TWorkerGroupConfig.SetApplicationFullPath(const Value: string);
begin
  FApplicationFullPath := Value;
end;

procedure TWorkerGroupConfig.SetMonitoringRate(const Value: Cardinal);
begin
  FMonitoringRate := Value;
end;

procedure TWorkerGroupConfig.SetName(const Value: string);
begin
  FName := Value;
end;

procedure TWorkerGroupConfig.SetTimeoutKeepAlive(const Value: Cardinal);
begin
  FTimeoutKeepAlive := Value;
end;

procedure TWorkerGroupConfig.SetTotalWorkers(const Value: integer);
begin
  FTotalWorkers := Value;
end;

end.
