// Flat JSON objects only. Command envelopes have bounded size and scalar fields.
string JsonEscape(string s){StringReplace(s,"\\","\\\\");StringReplace(s,"\"","\\\"");StringReplace(s,"\r","\\r");StringReplace(s,"\n","\\n");return s;}
string JsonString(string s){return "\""+JsonEscape(s)+"\"";}
void JsonWhite(const string s,int &i){while(i<StringLen(s)&&StringGetCharacter(s,i)<=32)i++;}
bool JsonQuoted(const string s,int &i,string &value)
{
   value="";if(i>=StringLen(s)||StringGetCharacter(s,i)!=34)return false;i++;
   while(i<StringLen(s))
   {
      ushort c=StringGetCharacter(s,i++);if(c==34)return true;
      if(c==92){if(i>=StringLen(s))return false;c=StringGetCharacter(s,i++);if(c==110)c=10;else if(c==114)c=13;else if(c==116)c=9;else if(c!=34&&c!=92&&c!=47)return false;}
      value+=ShortToString(c);
   }
   return false;
}
bool JsonGet(const string s,const string wanted,string &value)
{
   int i=0;JsonWhite(s,i);if(i>=StringLen(s)||StringGetCharacter(s,i)!=123)return false;i++;
   while(i<StringLen(s))
   {
      JsonWhite(s,i);if(StringGetCharacter(s,i)==125)return false;string key;if(!JsonQuoted(s,i,key))return false;
      JsonWhite(s,i);if(StringGetCharacter(s,i++)!=58)return false;JsonWhite(s,i);string v;
      if(StringGetCharacter(s,i)==34){if(!JsonQuoted(s,i,v))return false;}
      else{int start=i;while(i<StringLen(s)&&StringGetCharacter(s,i)!=44&&StringGetCharacter(s,i)!=125)i++;v=StringSubstr(s,start,i-start);StringTrimRight(v);}
      if(key==wanted){value=v;return true;}
      JsonWhite(s,i);if(i>=StringLen(s)||StringGetCharacter(s,i)==125)return false;if(StringGetCharacter(s,i++)!=44)return false;
   }
   return false;
}
string JString(const string s,const string key){string v;if(!JsonGet(s,key,v))return "";return v;}
long JLong(const string s,const string key){return StringToInteger(JString(s,key));}
double JDouble(const string s,const string key){return StringToDouble(JString(s,key));}
