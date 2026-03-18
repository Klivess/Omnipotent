using Omnipotent.Services.KlivesWorkoutManager;

namespace Omnipotent.Tests.KlivesWorkoutManager
{
    public class StrengthLevelTests
    {
        #region CalculateOneRepMax

        [Fact]
        public void CalculateOneRepMax_SingleRep_ReturnsWeight()
        {
            double result = StrengthLevel.CalculateOneRepMax(100, 1);
            Assert.Equal(100, result);
        }

        [Fact]
        public void CalculateOneRepMax_AccurateToHevy()
        {
            double result = StrengthLevel.CalculateOneRepMax(59, 6);
            Assert.True(result > 68 && result < 69);
        }

        [Fact]
        public void CalculateOneRepMax_MultipleReps_ReturnsHigherThanWeight()
        {
            double result = StrengthLevel.CalculateOneRepMax(100, 5);
            Assert.True(result > 100);
        }

        [Fact]
        public void CalculateOneRepMax_ZeroReps_ReturnsZero()
        {
            double result = StrengthLevel.CalculateOneRepMax(100, 0);
            Assert.Equal(0, result);
        }

        [Fact]
        public void CalculateOneRepMax_NegativeReps_ReturnsZero()
        {
            double result = StrengthLevel.CalculateOneRepMax(100, -5);
            Assert.Equal(0, result);
        }

        [Fact]
        public void CalculateOneRepMax_ZeroWeight_ReturnsZero()
        {
            double result = StrengthLevel.CalculateOneRepMax(0, 10);
            Assert.Equal(0, result);
        }

        [Fact]
        public void CalculateOneRepMax_NegativeWeight_ReturnsZero()
        {
            double result = StrengthLevel.CalculateOneRepMax(-50, 5);
            Assert.Equal(0, result);
        }

        #endregion

        #region StrengthLevelRequest

        [Fact]
        public void StrengthLevelRequest_DefaultTimezone_IsZero()
        {
            var request = new StrengthLevel.StrengthLevelRequest();
            Assert.Equal(0, request.Timezone);
        }

        [Fact]
        public void StrengthLevelRequest_PropertiesSetCorrectly()
        {
            var request = new StrengthLevel.StrengthLevelRequest
            {
                Gender = "male",
                AgeYears = 25,
                BodyMassKg = 80,
                Exercise = "bench-press",
                LiftMassKg = 100,
                Repetitions = 5,
            };

            Assert.Equal("male", request.Gender);
            Assert.Equal(25, request.AgeYears);
            Assert.Equal(80, request.BodyMassKg);
            Assert.Equal("bench-press", request.Exercise);
            Assert.Equal(100, request.LiftMassKg);
            Assert.Equal(5, request.Repetitions);
        }

        #endregion

        #region StrengthLevelResponse defaults

        [Fact]
        public void StrengthLevelResponse_DefaultsAreEmpty()
        {
            var response = new StrengthLevel.StrengthLevelResponse();
            Assert.Equal("", response.Exercise);
            Assert.Equal("", response.Level);
            Assert.Equal(0, response.Stars);
            Assert.Equal("", response.StrongerThanPercentage);
            Assert.Equal("", response.StrongerThanDescription);
            Assert.Equal("", response.BodyweightRatio);
            Assert.NotNull(response.Standards);
        }

        #endregion

        #region StrengthStandards

        [Fact]
        public void StrengthStandards_AllLevelsAreZeroByDefault()
        {
            var standards = new StrengthLevel.StrengthStandards();
            Assert.Equal(0, standards.Bodyweight);
            Assert.Equal(0, standards.Beginner);
            Assert.Equal(0, standards.Novice);
            Assert.Equal(0, standards.Intermediate);
            Assert.Equal(0, standards.Advanced);
            Assert.Equal(0, standards.Elite);
        }

        #endregion
    }
}
