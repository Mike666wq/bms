using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace BmsSerialDemo
{
    sealed class TrendControl : Control
    {
        sealed class PointValue { public double Voltage, Current; public int Soc, Sequence; public DateTime Utc; }
        readonly Dictionary<int, List<PointValue>> history = new Dictionary<int, List<PointValue>>();
        readonly Dictionary<int, int> sequence = new Dictionary<int, int>();
        int capacity = 120, seriesIndex; bool fixedRange; DateTime rangeStart,rangeEnd; int selectedPack=1;
        public int Capacity { get { return capacity; } set { capacity=Math.Max(1,Math.Min(600,value)); Trim(); Invalidate(); } }
        public int SelectedPack { get { return selectedPack; } set { if(selectedPack==value)return;selectedPack=value;RestoreFullRange(); } }
        public int SeriesIndex { get { return seriesIndex; } set { seriesIndex=Math.Max(0,Math.Min(2,value)); Invalidate(); } }
        public bool CurrentSeries { get { return seriesIndex==1; } set { seriesIndex=value?1:0; Invalidate(); } }
        public TrendControl() { BackColor=Color.White; ForeColor=Color.FromArgb(44,65,89); DoubleBuffered=true; MinimumSize=new Size(240,145); }
        public void Add(int pack,double voltage,double current) { Add(pack,voltage,current,0,DateTime.UtcNow); }
        public void Add(int pack,double voltage,double current,int soc,DateTime receivedUtc){List<PointValue> list;if(!history.TryGetValue(pack,out list))history[pack]=list=new List<PointValue>();int n;sequence.TryGetValue(pack,out n);n++;sequence[pack]=n;list.Add(new PointValue{Voltage=voltage,Current=current,Soc=soc,Sequence=n,Utc=receivedUtc.ToUniversalTime()});while(list.Count>capacity)list.RemoveAt(0);ConstrainRangeToData(pack);Invalidate();}
        public void Clear(int pack) { history.Remove(pack);sequence.Remove(pack);if(pack==SelectedPack)RestoreFullRange();else Invalidate(); }
        void Trim(){foreach(KeyValuePair<int,List<PointValue>> pair in history){while(pair.Value.Count>capacity)pair.Value.RemoveAt(0);ConstrainRangeToData(pair.Key);}}
        public void RestoreFullRange(){fixedRange=false;Invalidate();}
        internal DateTime RangeStartForTests{get{DateTime a,b;GetRange(out a,out b);return a;}}
        internal DateTime RangeEndForTests{get{DateTime a,b;GetRange(out a,out b);return b;}}
        internal Point PlotCenterForTests{get{Rectangle r=PlotRectangle;return new Point(r.Left+r.Width/2,r.Top+r.Height/2);}}
        Rectangle PlotRectangle{get{return new Rectangle(65,24,Math.Max(80,Width-88),Math.Max(55,Height-65));}}
        void GetRange(out DateTime from,out DateTime to){List<PointValue> list;if(!history.TryGetValue(SelectedPack,out list)||list.Count==0){to=DateTime.UtcNow;from=to.AddMinutes(-1);return;}DateTime first=list.Min(x=>x.Utc),last=list.Max(x=>x.Utc);if(!fixedRange){from=first;to=last;if(from==to)from=to.AddSeconds(-1);return;}from=rangeStart;to=rangeEnd;}
        protected override void OnMouseWheel(MouseEventArgs e){HandledMouseEventArgs handled=e as HandledMouseEventArgs;if((ModifierKeys&Keys.Control)==Keys.Control){ZoomAt(e.Location,e.Delta);Invalidate();if(handled!=null)handled.Handled=true;base.OnMouseWheel(e);return;}base.OnMouseWheel(e);}
        void ZoomAt(Point point,int delta){List<PointValue> list;if(!history.TryGetValue(SelectedPack,out list)||list.Count<2)return;DateTime first=list.Min(x=>x.Utc),last=list.Max(x=>x.Utc);double span=(last-first).TotalSeconds;if(span<2)return;DateTime a,b;GetRange(out a,out b);double old=Math.Max(1,(b-a).TotalSeconds),next=Math.Max(2,Math.Min(span,old*(delta>0?.75:1.33)));Rectangle plot=PlotRectangle;double anchor=Math.Max(0,Math.Min(1,(double)(point.X-plot.Left)/Math.Max(1,plot.Width)));DateTime proposed=a.AddSeconds((old-next)*anchor);double offset=Math.Max(0,Math.Min(Math.Max(0,span-next),(proposed-first).TotalSeconds));rangeStart=first.AddSeconds(offset);rangeEnd=rangeStart.AddSeconds(next);fixedRange=true;}
        void ConstrainRangeToData(int pack){if(!fixedRange||pack!=SelectedPack)return;List<PointValue> list;if(!history.TryGetValue(pack,out list)||list.Count<2){RestoreFullRange();return;}DateTime first=list.Min(x=>x.Utc),last=list.Max(x=>x.Utc);double span=(last-first).TotalSeconds,window=Math.Max(2,(rangeEnd-rangeStart).TotalSeconds);if(span<2){RestoreFullRange();return;}window=Math.Min(window,span);double offset=(rangeStart-first).TotalSeconds;offset=Math.Max(0,Math.Min(span-window,offset));rangeStart=first.AddSeconds(offset);rangeEnd=rangeStart.AddSeconds(window);}
        DateTime lastClickUtc;Point lastClickPos;
        protected override void OnMouseUp(MouseEventArgs e){if(e.Button==MouseButtons.Left){DateTime now=DateTime.UtcNow;if((now-lastClickUtc).TotalMilliseconds<=650&&Math.Abs(e.X-lastClickPos.X)<=6&&Math.Abs(e.Y-lastClickPos.Y)<=6){lastClickUtc=DateTime.MinValue;RestoreFullRange();}else{lastClickUtc=now;lastClickPos=e.Location;}}base.OnMouseUp(e);}
        protected override void OnDoubleClick(EventArgs e){RestoreFullRange();base.OnDoubleClick(e);}
        double Value(PointValue p){return seriesIndex==0?p.Voltage:seriesIndex==1?p.Current:p.Soc;}
        string Unit { get { return seriesIndex==0?"V":seriesIndex==1?"A":"%"; } }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);Graphics g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;g.Clear(Color.White);
            Rectangle plot=PlotRectangle;Color gridColor=Color.FromArgb(226,233,241),inkColor=Color.FromArgb(105,122,143),lineColor=Color.FromArgb(40,119,190);
            using(Pen grid=new Pen(gridColor,1))using(Pen axis=new Pen(Color.FromArgb(177,193,211),1))using(Pen line=new Pen(lineColor,2.4f))using(Brush ink=new SolidBrush(inkColor))using(Brush area=new SolidBrush(Color.FromArgb(22,60,145,218)))using(Font tickFont=new Font("Microsoft YaHei UI",8))
            {
                List<PointValue> all;if(!history.TryGetValue(SelectedPack,out all)||all.Count==0){for(int i=0;i<=4;i++){int y=plot.Top+plot.Height*i/4;g.DrawLine(grid,plot.Left,y,plot.Right,y);}g.DrawString("等待 Pack "+SelectedPack+" 实时采样",Font,ink,plot.Left+10,plot.Top+12);return;}DateTime from,to;GetRange(out from,out to);List<PointValue> list=all.Where(p=>p.Utc>=from&&p.Utc<=to).ToList();if(list.Count==0){for(int i=0;i<=4;i++){int y=plot.Top+plot.Height*i/4;g.DrawLine(grid,plot.Left,y,plot.Right,y);}g.DrawString("当前时间范围没有可见样本",Font,ink,plot.Left+10,plot.Top+12);return;}
                double min=Double.MaxValue,max=Double.MinValue;foreach(PointValue p in list){double v=Value(p);min=Math.Min(min,v);max=Math.Max(max,v);}double range=Math.Max(max-min,seriesIndex==1?.1:seriesIndex==2?1:.05);min-=range*.12;max+=range*.12;range=max-min;
                string valueFormat=seriesIndex==2?"0":range<0.1?"0.0000":range<1.0?"0.000":"0.00";
                for(int i=0;i<=4;i++){double v=max-range*i/4.0;int y=plot.Top+plot.Height*i/4;g.DrawLine(grid,plot.Left,y,plot.Right,y);string label=v.ToString(valueFormat,CultureInfo.InvariantCulture);SizeF size=g.MeasureString(label,tickFont);g.DrawString(label,tickFont,ink,plot.Left-size.Width-7,y-size.Height/2);}
                g.DrawLine(axis,plot.Left,plot.Top,plot.Left,plot.Bottom);g.DrawLine(axis,plot.Left,plot.Bottom,plot.Right,plot.Bottom);
                long first=from.Ticks,last=to.Ticks;double span=Math.Max(1,last-first);PointF[] pts=new PointF[list.Count];
                for(int i=0;i<list.Count;i++){double x=(list[i].Utc.Ticks-first)/span;double v=Value(list[i]);pts[i]=new PointF(plot.Left+(float)x*plot.Width,plot.Bottom-(float)((v-min)/range*plot.Height));}
                if(pts.Length>1){PointF[] fill=new PointF[pts.Length+2];Array.Copy(pts,fill,pts.Length);fill[pts.Length]=new PointF(pts[pts.Length-1].X,plot.Bottom);fill[pts.Length+1]=new PointF(pts[0].X,plot.Bottom);g.FillPolygon(area,fill);g.DrawLines(line,pts);}
                PointF latestPoint=pts[pts.Length-1];g.FillEllipse(Brushes.White,latestPoint.X-4,latestPoint.Y-4,8,8);g.DrawEllipse(line,latestPoint.X-4,latestPoint.Y-4,8,8);
                string latest=Value(list[list.Count-1]).ToString(valueFormat,CultureInfo.InvariantCulture)+" "+Unit;using(Font latestFont=new Font("Microsoft YaHei UI",9,FontStyle.Bold))g.DrawString(latest,latestFont,Brushes.SteelBlue,plot.Right-80,plot.Top-1);
                string t0=from.ToLocalTime().ToString("HH:mm:ss"),t1=to.ToLocalTime().ToString("HH:mm:ss");g.DrawString(t0,tickFont,ink,plot.Left,plot.Bottom+4);SizeF endSize=g.MeasureString(t1,tickFont);g.DrawString(t1,tickFont,ink,plot.Right-endSize.Width,plot.Bottom+4);
                string caption="本地采样时间 · "+list.Count+"/"+capacity+" 点";SizeF cap=g.MeasureString(caption,tickFont);g.DrawString(caption,tickFont,ink,(plot.Left+plot.Right-cap.Width)/2,plot.Bottom+4);
                g.DrawString((seriesIndex==0?"总压":seriesIndex==1?"电流":"SOC")+" ("+Unit+")",tickFont,ink,1,2);
            }
        }
    }
}
