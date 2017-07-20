using System;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Http.ModelBinding;
using System.Web.Security;
using Microsoft.AspNet.Identity;
using umbraco.cms.businesslogic.packager;
using Umbraco.Core;
using Umbraco.Core.Logging;
using Umbraco.Core.Models.Identity;
using Umbraco.Core.Security;
using Umbraco.Core.Services;
using Umbraco.Web.Models;
using Umbraco.Web.Security;
using IUser = Umbraco.Core.Models.Membership.IUser;

namespace Umbraco.Web.Editors
{
    internal class PasswordChanger
    {
        private readonly ILogger _logger;
        private readonly IUserService _userService;

        public PasswordChanger(ILogger logger, IUserService userService)
        {
            _logger = logger;
            _userService = userService;
        }
        
        public async Task<Attempt<PasswordChangedModel>> ChangePasswordWithIdentityAsync(
            IUser currentUser,
            ChangingPasswordModel passwordModel, 
            ModelStateDictionary modelState, 
            BackOfficeUserManager<BackOfficeIdentityUser> userMgr)
        {
            if (passwordModel == null) throw new ArgumentNullException("passwordModel");
            if (userMgr == null) throw new ArgumentNullException("userMgr");

            //check if this identity implementation is powered by an underlying membership provider (it will be in most cases)
            var membershipPasswordHasher = userMgr.PasswordHasher as IMembershipProviderPasswordHasher;

            //check if this identity implementation is powered by an IUserAwarePasswordHasher (it will be by default in 7.7+ but not for upgrades)
            var userAwarePasswordHasher = userMgr.PasswordHasher as IUserAwarePasswordHasher<int>;

            if (membershipPasswordHasher != null && userAwarePasswordHasher == null)
            {
                //if this isn't using an IUserAwarePasswordHasher, then fallback to the old way
                if (membershipPasswordHasher.MembershipProvider.RequiresQuestionAndAnswer)
                    throw new NotSupportedException("Currently the user editor does not support providers that have RequiresQuestionAndAnswer specified");
                return ChangePasswordWithMembershipProvider(currentUser.Username, passwordModel, membershipPasswordHasher.MembershipProvider);
            }            

            //get the real password validator, thsi should not be null but in some very rare cases it could be, in which case
            //we need to create a default password validator to use since we have no idea what it actually is or what it's rules are
            //this is an Edge Case!
            var passwordValidator = userMgr.PasswordValidator as PasswordValidator
                                    ?? (membershipPasswordHasher != null
                                        ? new MembershipProviderPasswordValidator(membershipPasswordHasher.MembershipProvider)
                                        : new PasswordValidator());

            //Are we resetting the password??
            if (passwordModel.Reset.HasValue && passwordModel.Reset.Value)
            {
                //ok, we should be able to reset it
                var resetToken = await userMgr.GeneratePasswordResetTokenAsync(currentUser.Id);
                var newPass = Membership.GeneratePassword(passwordValidator.RequiredLength, passwordValidator.RequireNonLetterOrDigit ? 2 : 0);
                var resetResult = await userMgr.ResetPasswordAsync(currentUser.Id, resetToken, newPass);

                if (resetResult.Succeeded == false)
                {
                    var errors = string.Join(". ", resetResult.Errors);
                    _logger.Warn<PasswordChanger>(string.Format("Could not reset member password {0}", errors));
                    return Attempt.Fail(new PasswordChangedModel { ChangeError = new ValidationResult("Could not reset password, errors: " + errors, new[] { "resetPassword" }) });
                }

                return Attempt.Succeed(new PasswordChangedModel { ResetPassword = newPass });
            }

            //we're not resetting it so we need to try to change it.

            if (passwordModel.NewPassword.IsNullOrWhiteSpace())
            {
                return Attempt.Fail(new PasswordChangedModel { ChangeError = new ValidationResult("Cannot set an empty password", new[] { "value" }) });
            }            

            //we cannot arbitrarily change the password without knowing the old one and no old password was supplied - need to return an error
            //TODO: What if the current user is admin? We should allow manually changing then?
            if (passwordModel.OldPassword.IsNullOrWhiteSpace())
            {
                //if password retrieval is not enabled but there is no old password we cannot continue
                return Attempt.Fail(new PasswordChangedModel { ChangeError = new ValidationResult("Password cannot be changed without the old password", new[] { "oldPassword" }) });
            }

            if (passwordModel.OldPassword.IsNullOrWhiteSpace() == false)
            {
                //if an old password is suplied try to change it
                var changeResult = await userMgr.ChangePasswordAsync(currentUser.Id, passwordModel.OldPassword, passwordModel.NewPassword);
                if (changeResult.Succeeded == false)
                {
                    var errors = string.Join(". ", changeResult.Errors);
                    _logger.Warn<PasswordChanger>(string.Format("Could not change member password {0}", errors));
                    return Attempt.Fail(new PasswordChangedModel { ChangeError = new ValidationResult("Could not change password, errors: " + errors, new[] { "value" }) });
                }
                return Attempt.Succeed(new PasswordChangedModel());                
            }

            //We shouldn't really get here
            return Attempt.Fail(new PasswordChangedModel { ChangeError = new ValidationResult("Could not change password, invalid information supplied", new[] { "value" }) });
        }

        /// <summary>
        /// Changes password for a member/user given the membership provider and the password change model
        /// </summary>
        /// <param name="username"></param>
        /// <param name="passwordModel"></param>
        /// <param name="membershipProvider"></param>
        /// <returns></returns>        
        public Attempt<PasswordChangedModel> ChangePasswordWithMembershipProvider(string username, ChangingPasswordModel passwordModel, MembershipProvider membershipProvider)
        {
            // YES! It is completely insane how many options you have to take into account based on the membership provider. yikes!        

            if (passwordModel == null) throw new ArgumentNullException("passwordModel");
            if (membershipProvider == null) throw new ArgumentNullException("membershipProvider");

            //Are we resetting the password??
            if (passwordModel.Reset.HasValue && passwordModel.Reset.Value)
            {
                var canReset = membershipProvider.CanResetPassword(_userService);
                if (canReset == false)
                {
                    return Attempt.Fail(new PasswordChangedModel { ChangeError = new ValidationResult("Password reset is not enabled", new[] { "resetPassword" }) });
                }
                if (membershipProvider.RequiresQuestionAndAnswer && passwordModel.Answer.IsNullOrWhiteSpace())
                {
                    return Attempt.Fail(new PasswordChangedModel { ChangeError = new ValidationResult("Password reset requires a password answer", new[] { "resetPassword" }) });
                }
                //ok, we should be able to reset it
                try
                {
                    var newPass = membershipProvider.ResetPassword(
                        username,
                        membershipProvider.RequiresQuestionAndAnswer ? passwordModel.Answer : null);

                    //return the generated pword
                    return Attempt.Succeed(new PasswordChangedModel { ResetPassword = newPass });
                }
                catch (Exception ex)
                {
                    _logger.WarnWithException<PasswordChanger>("Could not reset member password", ex);
                    return Attempt.Fail(new PasswordChangedModel { ChangeError = new ValidationResult("Could not reset password, error: " + ex.Message + " (see log for full details)", new[] { "resetPassword" }) });
                }
            }

            //we're not resetting it so we need to try to change it.

            if (passwordModel.NewPassword.IsNullOrWhiteSpace())
            {
                return Attempt.Fail(new PasswordChangedModel { ChangeError = new ValidationResult("Cannot set an empty password", new[] { "value" }) });
            }

            //This is an edge case and is only necessary for backwards compatibility:
            var umbracoBaseProvider = membershipProvider as MembershipProviderBase;
            if (umbracoBaseProvider != null && umbracoBaseProvider.AllowManuallyChangingPassword)
            {
                //this provider allows manually changing the password without the old password, so we can just do it
                try
                {
                    var result = umbracoBaseProvider.ChangePassword(username, "", passwordModel.NewPassword);
                    return result == false
                        ? Attempt.Fail(new PasswordChangedModel { ChangeError = new ValidationResult("Could not change password, invalid username or password", new[] { "value" }) })
                        : Attempt.Succeed(new PasswordChangedModel());
                }
                catch (Exception ex)
                {
                    _logger.WarnWithException<PasswordChanger>("Could not change member password", ex);
                    return Attempt.Fail(new PasswordChangedModel { ChangeError = new ValidationResult("Could not change password, error: " + ex.Message + " (see log for full details)", new[] { "value" }) });
                }
            }

            //The provider does not support manually chaning the password but no old password supplied - need to return an error
            if (passwordModel.OldPassword.IsNullOrWhiteSpace() && membershipProvider.EnablePasswordRetrieval == false)
            {
                //if password retrieval is not enabled but there is no old password we cannot continue
                return Attempt.Fail(new PasswordChangedModel { ChangeError = new ValidationResult("Password cannot be changed without the old password", new[] { "oldPassword" }) });
            }

            if (passwordModel.OldPassword.IsNullOrWhiteSpace() == false)
            {
                //if an old password is suplied try to change it

                try
                {
                    var result = membershipProvider.ChangePassword(username, passwordModel.OldPassword, passwordModel.NewPassword);
                    return result == false
                        ? Attempt.Fail(new PasswordChangedModel { ChangeError = new ValidationResult("Could not change password, invalid username or password", new[] { "oldPassword" }) })
                        : Attempt.Succeed(new PasswordChangedModel());
                }
                catch (Exception ex)
                {
                    _logger.WarnWithException<PasswordChanger>("Could not change member password", ex);
                    return Attempt.Fail(new PasswordChangedModel { ChangeError = new ValidationResult("Could not change password, error: " + ex.Message + " (see log for full details)", new[] { "value" }) });
                }
            }

            if (membershipProvider.EnablePasswordRetrieval == false)
            {
                //we cannot continue if we cannot get the current password
                return Attempt.Fail(new PasswordChangedModel { ChangeError = new ValidationResult("Password cannot be changed without the old password", new[] { "oldPassword" }) });
            }
            if (membershipProvider.RequiresQuestionAndAnswer && passwordModel.Answer.IsNullOrWhiteSpace())
            {
                //if the question answer is required but there isn't one, we cannot continue
                return Attempt.Fail(new PasswordChangedModel { ChangeError = new ValidationResult("Password cannot be changed without the password answer", new[] { "value" }) });
            }

            //lets try to get the old one so we can change it
            try
            {
                var oldPassword = membershipProvider.GetPassword(
                    username,
                    membershipProvider.RequiresQuestionAndAnswer ? passwordModel.Answer : null);

                try
                {
                    var result = membershipProvider.ChangePassword(username, oldPassword, passwordModel.NewPassword);
                    return result == false
                        ? Attempt.Fail(new PasswordChangedModel { ChangeError = new ValidationResult("Could not change password", new[] { "value" }) })
                        : Attempt.Succeed(new PasswordChangedModel());
                }
                catch (Exception ex1)
                {
                    _logger.WarnWithException<PasswordChanger>("Could not change member password", ex1);
                    return Attempt.Fail(new PasswordChangedModel { ChangeError = new ValidationResult("Could not change password, error: " + ex1.Message + " (see log for full details)", new[] { "value" }) });
                }

            }
            catch (Exception ex2)
            {
                _logger.WarnWithException<PasswordChanger>("Could not retrieve member password", ex2);
                return Attempt.Fail(new PasswordChangedModel { ChangeError = new ValidationResult("Could not change password, error: " + ex2.Message + " (see log for full details)", new[] { "value" }) });
            }
        }

    }
}