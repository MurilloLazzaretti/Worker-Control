unit WorkerControl.BoostWorkerGroup;

interface

type
  TBoostWorkerGroup = class
  private
    FEnabled: Boolean;
    FStartTime: TTime;
    FEndTime: TTime;
    FBoostWorkers: integer;
    procedure SetBoostWorkers(const Value: integer);
    procedure SetEnabled(const Value: Boolean);
    procedure SetEndTime(const Value: TTime);
    procedure SetStartTime(const Value: TTime);
  public
    property Enabled : Boolean read FEnabled write SetEnabled;
    property BoostWorkers : integer read FBoostWorkers write SetBoostWorkers;
    property StartTime : TTime read FStartTime write SetStartTime;
    property EndTime : TTime read FEndTime write SetEndTime;
  end;

implementation

{ TBoostWorkerGroup }

procedure TBoostWorkerGroup.SetBoostWorkers(const Value: integer);
begin
  FBoostWorkers := Value;
end;

procedure TBoostWorkerGroup.SetEnabled(const Value: Boolean);
begin
  FEnabled := Value;
end;

procedure TBoostWorkerGroup.SetEndTime(const Value: TTime);
begin
  FEndTime := Value;
end;

procedure TBoostWorkerGroup.SetStartTime(const Value: TTime);
begin
  FStartTime := Value;
end;

end.
