unit WorkerControl.KeepAliveMessage;

interface

uses
  JSON;

type
  TKeepAliveMessage = class
  private
    FProcessId: string;
    procedure SetProcessId(const Value: string);
  public
    property ProcessId : string read FProcessId write SetProcessId;
    function ToJSON : TJSONObject;
    class function FromJSON(const pJSON : TJSONObject) : TKeepAliveMessage;
  end;

implementation

uses
  System.SysUtils;

{ TKeepAliveMessage }

class function TKeepAliveMessage.FromJSON(
  const pJSON: TJSONObject): TKeepAliveMessage;
var
  Response : TJSONObject;
begin
  Result := TKeepAliveMessage.Create;
  try
    Response := TJSONObject.ParseJSONValue(
      TEncoding.ASCII.GetBytes(pJSON.GetValue<TJSONObject>('Response').ToString), 0) as TJSONObject;
    try
      Result.ProcessId := Response.GetValue<string>('ProcessId');
    finally
      Response.Free;
    end;
  except
    Result.ProcessId := pJSON.GetValue<string>('ProcessId');
  end;
end;

procedure TKeepAliveMessage.SetProcessId(const Value: string);
begin
  FProcessId := Value;
end;

function TKeepAliveMessage.ToJSON: TJSONObject;
begin
  Result := TJSONObject.Create;
  Result.AddPair('ProcessId', TJSONString.Create(FProcessId));
end;

end.
