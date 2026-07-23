using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PalCalc.Model.Tests
{
    [TestClass]
    public class PalTestBase
    {
        private static PalDB? defaultDb;
        private static PalBreedingDB? defaultBreedingDb;

        protected PalDB paldb => defaultDb ?? throw new InvalidOperationException("The test database has not been initialized.");
        protected PalBreedingDB breedingdb => defaultBreedingDb ?? throw new InvalidOperationException("The test breeding database has not been initialized.");

        [AssemblyInitialize]
        public static async Task AssemblyInit(TestContext context)
        {
            defaultDb = PalDB.LoadEmbedded();
            defaultBreedingDb = PalBreedingDB.LoadEmbedded(defaultDb);
        }
    }
}
