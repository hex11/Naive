using Naive.HttpSvr;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace NaiveServer
{
    public interface IModule
    {
        void Load(IController controller);
        void Start();
    }
}
