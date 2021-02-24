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
    procedure SetApplicationFullPath(const Value: string);
    procedure SetName(const Value: string);
    procedure SetTotalWorkers(const Value: integer);
  public
    property Name : string read FName write SetName;
    property ApplicationFullPath : string read FApplicationFullPath write SetApplicationFullPath;
    property TotalWorkers : integer read FTotalWorkers write SetTotalWorkers;
  end;

  TConfig = class
  private
    FWorkerGroupsConfig: TObjectList<TWorkerGroupConfig>;
    procedure SetWorkerGroupsConfig(
      const Value: TObjectList<TWorkerGroupConfig>);
  public
    property WorkerGroupsConfig : TObjectList<TWorkerGroupConfig> read FWorkerGroupsConfig write SetWorkerGroupsConfig;
    constructor Create; overload;
    destructor Destroy; override;
    class function FromJSON(const pJSON : TJSONObject) : TConfig;
  end;

implementation

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

class function TConfig.FromJSON(const pJSON: TJSONObject): TConfig;
var
  JSONArray : TJSONArray;
  i: Integer;
  WorkerGroupConfig : TWorkerGroupConfig;
begin
  Result := TConfig.Create;
  JSONArray := pJSON.GetValue<TJSONArray>('WorkerGroups');
  for i := 0 to Pred(JSONArray.Count) do
  begin
    WorkerGroupConfig := TWorkerGroupConfig.Create;
    WorkerGroupConfig.Name := JSONArray.Items[i].GetValue<string>('Name');
    WorkerGroupConfig.ApplicationFullPath := JSONArray.Items[i].GetValue<string>('ApplicationFullPath');
    WorkerGroupConfig.TotalWorkers := JSONArray.Items[i].GetValue<integer>('TotalWorkers');
    Result.WorkerGroupsConfig.Add(WorkerGroupConfig);
  end;
end;

procedure TConfig.SetWorkerGroupsConfig(
  const Value: TObjectList<TWorkerGroupConfig>);
begin
  FWorkerGroupsConfig := Value;
end;

{ TWorkerGroupConfig }

procedure TWorkerGroupConfig.SetApplicationFullPath(const Value: string);
begin
  FApplicationFullPath := Value;
end;

procedure TWorkerGroupConfig.SetName(const Value: string);
begin
  FName := Value;
end;

procedure TWorkerGroupConfig.SetTotalWorkers(const Value: integer);
begin
  FTotalWorkers := Value;
end;

end.
