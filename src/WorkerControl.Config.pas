unit WorkerControl.Config;

interface

uses
  Generics.Collections, JSON;

type
  TBoostWorkerGroupConfig = class
  private
    FEnabled: boolean;
    FStartTime: TTime;
    FEndTime: TTime;
    FBoostWorkers: integer;
    procedure SetBoostWorkers(const Value: integer);
    procedure SetEnabled(const Value: boolean);
    procedure SetEndTime(const Value: TTime);
    procedure SetStartTime(const Value: TTime);
  public
    property Enabled : boolean read FEnabled write SetEnabled;
    property BoostWorkers : integer read FBoostWorkers write SetBoostWorkers;
    property StartTime : TTime read FStartTime write SetStartTime;
    property EndTime : TTime read FEndTime write SetEndTime;
  end;

  TWorkerGroupConfig = class
  private
    FTotalWorkers: integer;
    FName: string;
    FApplicationFullPath: string;
    FTimeoutKeepAlive: Cardinal;
    FMonitoringRate: Cardinal;
    FEnabled: boolean;
    FBoost: TBoostWorkerGroupConfig;
    procedure SetApplicationFullPath(const Value: string);
    procedure SetName(const Value: string);
    procedure SetTotalWorkers(const Value: integer);
    procedure SetMonitoringRate(const Value: Cardinal);
    procedure SetTimeoutKeepAlive(const Value: Cardinal);
    procedure SetEnabled(const Value: boolean);
    procedure SetBoost(const Value: TBoostWorkerGroupConfig);
  public
    property Enabled : boolean read FEnabled write SetEnabled;
    property Name : string read FName write SetName;
    property ApplicationFullPath : string read FApplicationFullPath write SetApplicationFullPath;
    property TotalWorkers : integer read FTotalWorkers write SetTotalWorkers;
    property MonitoringRate : Cardinal read FMonitoringRate write SetMonitoringRate;
    property TimeoutKeepAlive : Cardinal read FTimeoutKeepAlive write SetTimeoutKeepAlive;
    property Boost : TBoostWorkerGroupConfig read FBoost write SetBoost;
    constructor Create; overload;
    destructor Destroy; override;
  end;

  TConfig = class
  private
    FWorkerGroupsConfig: TObjectList<TWorkerGroupConfig>;
    FZapMQPort: integer;
    FZapMQHost: string;
    FRateLoadConfig: Cardinal;
    procedure SetWorkerGroupsConfig(
      const Value: TObjectList<TWorkerGroupConfig>);
    procedure SetZapMQHost(const Value: string);
    procedure SetZapMQPort(const Value: integer);
    procedure SetRateLoadConfig(const Value: Cardinal);
  public
    property ZapMQHost : string read FZapMQHost write SetZapMQHost;
    property ZapMQPort : integer read FZapMQPort write SetZapMQPort;
    property RateLoadConfig : Cardinal read FRateLoadConfig write SetRateLoadConfig;
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
  JSONRootObject, JSONBoostObject : TJSONObject;
  i: Integer;
  WorkerGroupConfig : TWorkerGroupConfig;
  fileName: TFileName;
begin
  Result := TConfig.Create;
  fileName := ExtractFilePath(Application.ExeName) + pFileName;
  if FileExists(fileName) then
  begin
    JSONRootObject := TJSONObject.ParseJSONValue(TFile.ReadAllText(fileName)) as TJSONObject;
    try
      Result.ZapMQHost := JSONRootObject.GetValue<string>('ZapMQHost');
      Result.ZapMQPort := JSONRootObject.GetValue<integer>('ZapMQPort');
      Result.RateLoadConfig := JSONRootObject.GetValue<Cardinal>('RateLoadConfig');
      JSONArray := JSONRootObject.GetValue<TJSONArray>('WorkerGroups');
      if JSONArray.Count > 0 then
      begin
        for i := 0 to Pred(JSONArray.Count) do
        begin
          WorkerGroupConfig := TWorkerGroupConfig.Create;
          WorkerGroupConfig.Enabled := JSONArray.Items[i].GetValue<boolean>('Enabled');
          WorkerGroupConfig.Name := JSONArray.Items[i].GetValue<string>('Name');
          WorkerGroupConfig.ApplicationFullPath := JSONArray.Items[i].GetValue<string>('ApplicationFullPath');
          WorkerGroupConfig.TotalWorkers := JSONArray.Items[i].GetValue<integer>('TotalWorkers');
          WorkerGroupConfig.MonitoringRate := JSONArray.Items[i].GetValue<Cardinal>('MonitoringRate');
          WorkerGroupConfig.TimeoutKeepAlive := JSONArray.Items[i].GetValue<Cardinal>('TimeoutKeepAlive');

          JSONBoostObject := JSONArray.Items[i].GetValue<TJSONObject>('Boost');
          WorkerGroupConfig.Boost.Enabled := JSONBoostObject.GetValue<boolean>('Enabled');
          WorkerGroupConfig.Boost.BoostWorkers := JSONBoostObject.GetValue<integer>('BoostWorkers');
          WorkerGroupConfig.Boost.StartTime := StrToTime(JSONBoostObject.GetValue<string>('StartTime'));
          WorkerGroupConfig.Boost.EndTime := StrToTime(JSONBoostObject.GetValue<string>('EndTime'));
          Result.WorkerGroupsConfig.Add(WorkerGroupConfig);
        end;
      end;
    finally
      JSONRootObject.Free;
    end;
  end;
end;

procedure TConfig.SetRateLoadConfig(const Value: Cardinal);
begin
  FRateLoadConfig := Value;
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

constructor TWorkerGroupConfig.Create;
begin
  FBoost := TBoostWorkerGroupConfig.Create;
end;

destructor TWorkerGroupConfig.Destroy;
begin
  FBoost.Free;
  inherited;
end;

procedure TWorkerGroupConfig.SetApplicationFullPath(const Value: string);
begin
  FApplicationFullPath := Value;
end;

procedure TWorkerGroupConfig.SetBoost(const Value: TBoostWorkerGroupConfig);
begin
  FBoost := Value;
end;

procedure TWorkerGroupConfig.SetEnabled(const Value: boolean);
begin
  FEnabled := Value;
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

{ TBoostWorkerGroupConfig }

procedure TBoostWorkerGroupConfig.SetBoostWorkers(const Value: integer);
begin
  FBoostWorkers := Value;
end;

procedure TBoostWorkerGroupConfig.SetEnabled(const Value: boolean);
begin
  FEnabled := Value;
end;

procedure TBoostWorkerGroupConfig.SetEndTime(const Value: TTime);
begin
  FEndTime := Value;
end;

procedure TBoostWorkerGroupConfig.SetStartTime(const Value: TTime);
begin
  FStartTime := Value;
end;

end.
