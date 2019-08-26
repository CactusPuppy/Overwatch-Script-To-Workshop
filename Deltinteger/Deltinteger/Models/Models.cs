using System;
using System.Collections.Generic;
using Deltin.Deltinteger;
using Deltin.Deltinteger.Models.Import;
using Deltin.Deltinteger.Elements;
using Deltin.Deltinteger.Parse;

namespace Deltin.Deltinteger.Models
{
    public class Model
    {
        public Line[] Lines { get; }

        private Model(Line[] lines)
        {
            Lines = lines;
        }

        public static Model ImportObj(string obj)
        {
            ObjModel result = ObjModel.Import(obj);
            return new Model(result.GetLines());
        }
    }

    public class Line
    {
        public Vertex Vertex1 { get; }
        public Vertex Vertex2 { get; }

        public Line(Vertex vertex1, Vertex vertex2)
        {
            Vertex1 = vertex1;
            Vertex2 = vertex2;
        }
    }

    public class Vertex
    {
        public double X { get; }
        public double Y { get; }
        public double Z { get; }
        public double W { get; }

        public Vertex(double x, double y, double z, double w)
        {
            X = x;
            Y = y;
            Z = z;
            W = w;
        }
        public Vertex(double x, double y, double z) : this(x,y,z,0) {}
        public Vertex() : this(0,0,0,0) {}

        public V_Vector ToVector()
        {
            return Element.Part<V_Vector>(new V_Number(X), new V_Number(Y), new V_Number(Z));
        }
    }

    class Face
    {
        public Vertex[] Vertices { get; }

        public Face(Vertex[] vertices)
        {
            Vertices = vertices;
        }

        public Line[] GetLines()
        {
            Line[] lines = new Line[Vertices.Length];
            for (int i = 0; i < Vertices.Length; i++)
            {
                int connectedIndex = 0;
                if (i != Vertices.Length - 1)
                    connectedIndex = i + 1;
                lines[i] = new Line(Vertices[i], Vertices[connectedIndex]);
            }
            return lines;
        }
    }

    interface IModelLoader
    {
        Line[] GetLines();
    }

    [CustomMethod("ShowWireframe", CustomMethodType.Action)]
    [VarRefParameter("Model")]
    [Parameter("Visible To", Elements.ValueType.Player, null)]
    [Parameter("Location", Elements.ValueType.Vector, null)]
    [Parameter("Scale", Elements.ValueType.Number, null)]
    class ShowModel : CustomMethodBase
    {
        override protected MethodResult Get()
        {
            if (((VarRef)Parameters[0]).Var is ModelVar == false)
                throw new SyntaxErrorException("", null);
            
            ModelVar modelVar = (ModelVar)((VarRef)Parameters[0]).Var;
            Element visibleTo = (Element)Parameters[1];
            Element location = (Element)Parameters[2];
            Element scale = (Element)Parameters[3];

            List<Element> actions = new List<Element>();
            foreach (Line line in modelVar.Model.Lines)
            {
                actions.Add(
                    Element.Part<A_CreateBeamEffect>(
                        visibleTo,
                        EnumData.GetEnumValue(BeamType.GrappleBeam),
                        Element.Part<V_Add>(location, Element.Part<V_Multiply>(line.Vertex1.ToVector(), scale)),
                        Element.Part<V_Add>(location, Element.Part<V_Multiply>(line.Vertex2.ToVector(), scale)),
                        EnumData.GetEnumValue(Color.LimeGreen),
                        EnumData.GetEnumValue(EffectRev.VisibleToPositionAndRadius)
                    )
                );
            }

            return new MethodResult(actions.ToArray(), null);
        }

        override public CustomMethodWiki Wiki()
        {
            return new CustomMethodWiki(
                "Create a wireframe of a variable containing a 3D model.",
                // Parameters
                "The variable containing the model constant.",
                "Who the model is visible to.",
                "The location of the model.",
                "The scale of the model."
            );
        }
    }
}