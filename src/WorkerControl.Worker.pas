unit WorkerControl.Worker;

interface

type
  TWorker = class
  private
    FProcessId: Cardinal;
    FLastKeepAlive: TDateTime;
    procedure SetLastKeepAlive(const Value: TDateTime);
    procedure SetProcessId(const Value: Cardinal);
  public
    property ProcessId : Cardinal read FProcessId write SetProcessId;
    property LastKeepAlive : TDateTime read FLastKeepAlive write SetLastKeepAlive;
  end;

implementation

{ TWorker }

procedure TWorker.SetLastKeepAlive(const Value: TDateTime);
begin
  FLastKeepAlive := Value;
end;

procedure TWorker.SetProcessId(const Value: Cardinal);
begin
  FProcessId := Value;
end;

end.
